using System.Text;

namespace NeoIDE.Controls.Terminal;

[Flags]
public enum CellAttr : byte { None = 0, Bold = 1, Underline = 2, Inverse = 4, Italic = 8, Dim = 16, Strike = 32 }

/// <summary>One character cell. Colours: -1 = default, 0..255 = palette, 0x1000000 | rgb = true colour.</summary>
public struct Cell
{
    public char Ch;
    public char Low;      // low surrogate (emoji...)
    public int Fg;
    public int Bg;
    public CellAttr Attr;

    public static Cell Blank(int bg = -1) => new() { Ch = ' ', Fg = -1, Bg = bg };
}

/// <summary>
/// The screen of a terminal: a VT100 / xterm parser (the subset ConPTY and the shells emit) updating a grid of cells,
/// with a scrollback of the main screen and the alternate screen of full-screen programs.
/// </summary>
public sealed class VtScreen
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AppCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AltScreen { get; private set; }
    public string Title { get; private set; } = "";
    public int MaxScrollback { get; set; } = 5000;

    public List<Cell[]> Scrollback { get; } = new();
    private Cell[][] _lines;
    private Cell[][]? _mainSaved;
    private int _scrollTop, _scrollBottom;
    private int _fg = -1, _bg = -1;
    private CellAttr _attr;
    private (int x, int y, int fg, int bg, CellAttr attr) _saved;
    private bool _wrapPending;

    /// <summary>Answers to send back to the program (cursor position report, device attributes).</summary>
    public event Action<string>? Reply;
    public event Action? TitleChanged;
    /// <summary>A line scrolled into the scrollback (the view keeps its position when the user scrolled up).</summary>
    public event Action? LineScrolledOff;

    public VtScreen(int cols, int rows)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(2, rows);
        _lines = NewLines(Rows);
        _scrollBottom = Rows - 1;
    }

    private Cell[] NewLine(int bg = -1)
    {
        var l = new Cell[Cols];
        for (int i = 0; i < l.Length; i++) l[i] = Cell.Blank(bg);
        return l;
    }

    private Cell[][] NewLines(int n)
    {
        var a = new Cell[n][];
        for (int i = 0; i < n; i++) a[i] = NewLine();
        return a;
    }

    public Cell[] Line(int y) => _lines[y];

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(2, rows);
        if (cols == Cols && rows == Rows) return;
        Cell[] Fit(Cell[] l)
        {
            if (l.Length == cols) return l;
            var n = new Cell[cols];
            for (int i = 0; i < cols; i++) n[i] = i < l.Length ? l[i] : Cell.Blank();
            return n;
        }
        var lines = _lines.Select(Fit).ToList();
        // fewer rows: the top lines go to the scrollback (only those above the cursor), more rows: blank lines at the bottom
        while (lines.Count > rows)
        {
            if (CursorY > 0 && !AltScreen) { Scrollback.Add(lines[0]); lines.RemoveAt(0); CursorY--; }
            else lines.RemoveAt(lines.Count - 1);
        }
        Cols = cols;
        while (lines.Count < rows) lines.Add(NewLine());
        Rows = rows;
        _lines = lines.ToArray();
        if (_mainSaved != null) _mainSaved = _mainSaved.Select(Fit).Take(rows).Concat(Enumerable.Range(0, Math.Max(0, rows - _mainSaved.Length)).Select(_ => NewLine())).ToArray();
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorX = Math.Min(CursorX, Cols - 1);
        CursorY = Math.Min(CursorY, Rows - 1);
        _wrapPending = false;
        TrimScrollback();
    }

    private void TrimScrollback()
    {
        if (Scrollback.Count > MaxScrollback) Scrollback.RemoveRange(0, Scrollback.Count - MaxScrollback);
    }

    public void ClearScrollback() => Scrollback.Clear();

    // ------------------------------------------------------------------ parser

    private enum State { Ground, Escape, EscapeCharset, Csi, Osc, OscEscape, Dcs, DcsEscape }
    private State _state;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _osc = new();
    private char _csiPrivate, _csiIntermediate;
    private char _pendingHigh;

    public void Feed(string data)
    {
        foreach (var c in data) Step(c);
    }

    private void Step(char c)
    {
        switch (_state)
        {
            case State.Ground:
                if (c == '\x1b') { _state = State.Escape; return; }
                if (c < ' ' || c == '\x7f') { Control(c); return; }
                Print(c);
                return;
            case State.Escape:
                Escape(c);
                return;
            case State.EscapeCharset:
                _state = State.Ground; // ESC ( B ... : the designated charset is ignored
                return;
            case State.Csi:
                if (c is >= '0' and <= '9' or ';' or ':') { _params.Append(c); return; }
                if (c is '?' or '>' or '=' or '<') { _csiPrivate = c; return; }
                if (c is >= ' ' and <= '/') { _csiIntermediate = c; return; }
                if (c is >= '@' and <= '~') { _state = State.Ground; Csi(c); return; }
                if (c == '\x1b') { _state = State.Escape; return; }
                if (c < ' ') { Control(c); return; }
                _state = State.Ground;
                return;
            case State.Osc:
                if (c == '\x07') { Osc(); _state = State.Ground; return; }
                if (c == '\x1b') { _state = State.OscEscape; return; }
                if (_osc.Length < 4096) _osc.Append(c);
                return;
            case State.OscEscape:
                if (c == '\\') { Osc(); _state = State.Ground; return; }
                _state = State.Escape;
                Escape(c);
                return;
            case State.Dcs:
                if (c == '\x1b') _state = State.DcsEscape;
                else if (c == '\x07') _state = State.Ground;
                return;
            case State.DcsEscape:
                _state = c == '\\' ? State.Ground : State.Dcs;
                return;
        }
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\r': CursorX = 0; _wrapPending = false; break;
            case '\n' or '\v' or '\f': LineFeed(); break;
            case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; break;
            case '\t':
                CursorX = Math.Min(Cols - 1, (CursorX / 8 + 1) * 8);
                _wrapPending = false;
                break;
            case '\x07': break; // bell
        }
    }

    private void Escape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[': _state = State.Csi; _params.Clear(); _csiPrivate = '\0'; _csiIntermediate = '\0'; break;
            case ']': _state = State.Osc; _osc.Clear(); break;
            case 'P': _state = State.Dcs; break;
            case '(' or ')' or '*' or '+': _state = State.EscapeCharset; break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'c': FullReset(); break;
            case '\\': break;
        }
    }

    private void Osc()
    {
        var s = _osc.ToString();
        var semi = s.IndexOf(';');
        if (semi > 0 && (s[..semi] == "0" || s[..semi] == "2"))
        {
            Title = s[(semi + 1)..];
            TitleChanged?.Invoke();
        }
    }

    private int[] Params(out bool any)
    {
        var raw = _params.ToString();
        any = raw.Length > 0;
        if (!any) return Array.Empty<int>();
        return raw.Split(';').Select(p => int.TryParse(p.Split(':')[0], out var v) ? v : -1).ToArray();
    }

    private static int P(int[] p, int i, int def) => i < p.Length && p[i] > 0 ? p[i] : def;

    private void Csi(char final)
    {
        var p = Params(out _);
        if (_csiPrivate == '?')
        {
            if (final is 'h' or 'l') foreach (var mode in p) PrivateMode(mode, final == 'h');
            return;
        }
        if (_csiPrivate is '>' or '=' or '<') return;
        if (_csiIntermediate != '\0') return; // DECSCUSR (" q"), soft reset ("! p")...
        switch (final)
        {
            case 'A': CursorY = Math.Max(CursorY >= _scrollTop ? _scrollTop : 0, CursorY - P(p, 0, 1)); _wrapPending = false; break;
            case 'B': CursorY = Math.Min(CursorY <= _scrollBottom ? _scrollBottom : Rows - 1, CursorY + P(p, 0, 1)); _wrapPending = false; break;
            case 'C' or 'a': CursorX = Math.Min(Cols - 1, CursorX + P(p, 0, 1)); _wrapPending = false; break;
            case 'D': CursorX = Math.Max(0, CursorX - P(p, 0, 1)); _wrapPending = false; break;
            case 'E': CursorY = Math.Min(Rows - 1, CursorY + P(p, 0, 1)); CursorX = 0; _wrapPending = false; break;
            case 'F': CursorY = Math.Max(0, CursorY - P(p, 0, 1)); CursorX = 0; _wrapPending = false; break;
            case 'G' or '`': CursorX = Math.Clamp(P(p, 0, 1) - 1, 0, Cols - 1); _wrapPending = false; break;
            case 'd': CursorY = Math.Clamp(P(p, 0, 1) - 1, 0, Rows - 1); _wrapPending = false; break;
            case 'e': CursorY = Math.Min(Rows - 1, CursorY + P(p, 0, 1)); _wrapPending = false; break;
            case 'H' or 'f':
                CursorY = Math.Clamp(P(p, 0, 1) - 1, 0, Rows - 1);
                CursorX = Math.Clamp(P(p, 1, 1) - 1, 0, Cols - 1);
                _wrapPending = false;
                break;
            case 'J': EraseDisplay(p.Length > 0 ? Math.Max(0, p[0]) : 0); break;
            case 'K': EraseLine(p.Length > 0 ? Math.Max(0, p[0]) : 0); break;
            case 'L': InsertLines(P(p, 0, 1)); break;
            case 'M': DeleteLines(P(p, 0, 1)); break;
            case 'P': DeleteChars(P(p, 0, 1)); break;
            case '@': InsertChars(P(p, 0, 1)); break;
            case 'X':
            {
                var n = P(p, 0, 1);
                var line = _lines[CursorY];
                for (int i = CursorX; i < Math.Min(Cols, CursorX + n); i++) line[i] = Cell.Blank(_bg);
                break;
            }
            case 'S': for (int i = 0; i < P(p, 0, 1); i++) ScrollUp(_scrollTop, _scrollBottom, false); break;
            case 'T': for (int i = 0; i < P(p, 0, 1); i++) ScrollDown(_scrollTop, _scrollBottom); break;
            case 'b':
            {
                var prev = CursorX > 0 ? _lines[CursorY][CursorX - 1].Ch : ' ';
                for (int i = 0; i < Math.Min(P(p, 0, 1), 2000); i++) Print(prev);
                break;
            }
            case 'm': Sgr(p); break;
            case 'r':
            {
                var top = P(p, 0, 1) - 1;
                var bottom = P(p, 1, Rows) - 1;
                if (top < bottom && bottom < Rows) { _scrollTop = top; _scrollBottom = bottom; }
                else { _scrollTop = 0; _scrollBottom = Rows - 1; }
                CursorX = 0;
                CursorY = 0;
                _wrapPending = false;
                break;
            }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'n':
                if (P(p, 0, 0) == 6) Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R");
                else if (P(p, 0, 0) == 5) Reply?.Invoke("\x1b[0n");
                break;
            case 'c': Reply?.Invoke("\x1b[?1;0c"); break;
        }
    }

    private void PrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 25: CursorVisible = on; break;
            case 2004: BracketedPaste = on; break;
            case 47 or 1047 or 1049:
                if (on == AltScreen) break;
                if (on)
                {
                    if (mode == 1049) SaveCursor();
                    _mainSaved = _lines;
                    _lines = NewLines(Rows);
                    AltScreen = true;
                }
                else
                {
                    _lines = _mainSaved ?? NewLines(Rows);
                    _mainSaved = null;
                    AltScreen = false;
                    if (mode == 1049) RestoreCursor();
                }
                _scrollTop = 0;
                _scrollBottom = Rows - 1;
                break;
        }
    }

    private void Sgr(int[] p)
    {
        if (p.Length == 0) { _fg = _bg = -1; _attr = CellAttr.None; return; }
        for (int i = 0; i < p.Length; i++)
        {
            var v = p[i] < 0 ? 0 : p[i];
            switch (v)
            {
                case 0: _fg = _bg = -1; _attr = CellAttr.None; break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Dim; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4: _attr |= CellAttr.Underline; break;
                case 7: _attr |= CellAttr.Inverse; break;
                case 9: _attr |= CellAttr.Strike; break;
                case 21 or 22: _attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 27: _attr &= ~CellAttr.Inverse; break;
                case 29: _attr &= ~CellAttr.Strike; break;
                case >= 30 and <= 37: _fg = v - 30; break;
                case 39: _fg = -1; break;
                case >= 40 and <= 47: _bg = v - 40; break;
                case 49: _bg = -1; break;
                case >= 90 and <= 97: _fg = v - 90 + 8; break;
                case >= 100 and <= 107: _bg = v - 100 + 8; break;
                case 38 or 48:
                {
                    int color = -1;
                    if (i + 1 < p.Length && p[i + 1] == 5 && i + 2 < p.Length) { color = Math.Clamp(p[i + 2], 0, 255); i += 2; }
                    else if (i + 1 < p.Length && p[i + 1] == 2 && i + 4 < p.Length)
                    {
                        color = 0x1000000 | (Math.Clamp(p[i + 2], 0, 255) << 16) | (Math.Clamp(p[i + 3], 0, 255) << 8) | Math.Clamp(p[i + 4], 0, 255);
                        i += 4;
                    }
                    if (v == 38) _fg = color; else _bg = color;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ operations

    private void Print(char c)
    {
        if (char.IsHighSurrogate(c)) { _pendingHigh = c; return; }
        char low = '\0';
        if (char.IsLowSurrogate(c) && _pendingHigh != '\0') { low = c; c = _pendingHigh; }
        _pendingHigh = '\0';
        if (_wrapPending)
        {
            CursorX = 0;
            LineFeed();
            _wrapPending = false;
        }
        _lines[CursorY][CursorX] = new Cell { Ch = c, Low = low, Fg = _fg, Bg = _bg, Attr = _attr };
        if (CursorX == Cols - 1) _wrapPending = true;
        else CursorX++;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (CursorY == _scrollBottom) ScrollUp(_scrollTop, _scrollBottom, true);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        if (CursorY == _scrollTop) ScrollDown(_scrollTop, _scrollBottom);
        else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int top, int bottom, bool keep)
    {
        var first = _lines[top];
        for (int y = top; y < bottom; y++) _lines[y] = _lines[y + 1];
        _lines[bottom] = NewLine(_bg);
        if (keep && top == 0 && !AltScreen)
        {
            Scrollback.Add(first);
            TrimScrollback();
            LineScrolledOff?.Invoke();
        }
    }

    private void ScrollDown(int top, int bottom)
    {
        for (int y = bottom; y > top; y--) _lines[y] = _lines[y - 1];
        _lines[top] = NewLine(_bg);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int y = CursorY + 1; y < Rows; y++) _lines[y] = NewLine(_bg);
                break;
            case 1:
                EraseLine(1);
                for (int y = 0; y < CursorY; y++) _lines[y] = NewLine(_bg);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) _lines[y] = NewLine(_bg);
                break;
            case 3:
                Scrollback.Clear();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var line = _lines[CursorY];
        var (from, to) = mode switch { 0 => (CursorX, Cols), 1 => (0, CursorX + 1), _ => (0, Cols) };
        for (int x = from; x < Math.Min(to, Cols); x++) line[x] = Cell.Blank(_bg);
        if (mode != 1) _wrapPending = false;
    }

    private void InsertLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        for (int i = 0; i < n; i++) ScrollDown(CursorY, _scrollBottom);
        CursorX = 0;
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        for (int i = 0; i < n; i++) ScrollUp(CursorY, _scrollBottom, false);
        CursorX = 0;
    }

    private void DeleteChars(int n)
    {
        var line = _lines[CursorY];
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(line, CursorX + n, line, CursorX, Cols - CursorX - n);
        for (int x = Cols - n; x < Cols; x++) line[x] = Cell.Blank(_bg);
        _wrapPending = false;
    }

    private void InsertChars(int n)
    {
        var line = _lines[CursorY];
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(line, CursorX, line, CursorX + n, Cols - CursorX - n);
        for (int x = CursorX; x < CursorX + n; x++) line[x] = Cell.Blank(_bg);
        _wrapPending = false;
    }

    private void SaveCursor() => _saved = (CursorX, CursorY, _fg, _bg, _attr);

    private void RestoreCursor()
    {
        (CursorX, CursorY, _fg, _bg, _attr) = _saved;
        CursorX = Math.Min(CursorX, Cols - 1);
        CursorY = Math.Min(CursorY, Rows - 1);
        _wrapPending = false;
    }

    private void FullReset()
    {
        _lines = NewLines(Rows);
        _mainSaved = null;
        AltScreen = false;
        CursorX = CursorY = 0;
        _fg = _bg = -1;
        _attr = CellAttr.None;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorVisible = true;
        AppCursorKeys = BracketedPaste = false;
    }

    /// <summary>Text of a line (trailing blanks removed).</summary>
    public static string TextOf(Cell[] line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line) { sb.Append(c.Ch == '\0' ? ' ' : c.Ch); if (c.Low != '\0') sb.Append(c.Low); }
        return sb.ToString().TrimEnd();
    }
}
