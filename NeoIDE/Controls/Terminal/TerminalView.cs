using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NeoIDE.Services;

namespace NeoIDE.Controls.Terminal;

public enum ShellKind { PowerShell, Pwsh, Cmd, GitBash, Wsl }

/// <summary>
/// A real terminal (ConPTY): PowerShell, cmd, Git Bash or WSL with colours, prompts, line editing, history,
/// full-screen programs; scrollback, mouse selection, copy / paste, Ctrl+click on file paths and URLs.
/// </summary>
public sealed class TerminalView : FrameworkElement
{
    private VtScreen _screen = new(120, 30);
    private PseudoConsole? _pty;
    private readonly object _pendingGate = new();
    private readonly StringBuilder _pending = new();
    private bool _flushQueued;
    private double _cellW = 8, _cellH = 17, _baseline = 13;
    private Typeface _regular = null!, _bold = null!, _italic = null!;
    private int _scroll;          // lines scrolled back from the bottom
    private bool _exited;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool _blinkOn = true;
    private (int row, int col)? _selStart, _selEnd;   // absolute rows (scrollback + screen)
    private bool _selecting;
    private const double PadX = 8, PadY = 4;

    public ShellKind Shell { get; }
    public string WorkingDirectory { get; private set; }
    public string Title => _screen.Title;
    public bool IsRunning => _pty != null && !_exited;
    public event Action? TitleChanged;
    /// <summary>Ctrl+click on a path ("src/Foo.php:12") or a URL.</summary>
    public event Action<string, string>? LinkClicked;   // (text, working directory)

    public TerminalView(ShellKind shell, string workingDirectory)
    {
        Shell = shell;
        WorkingDirectory = workingDirectory;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UpdateFont();
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; InvalidateVisual(); };
        GotKeyboardFocus += (_, _) => { _blinkOn = true; _blink.Start(); InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => { _blink.Stop(); _blinkOn = true; InvalidateVisual(); };
        SizeChanged += (_, _) => OnSize();
        ThemeManager.ThemeChanged += InvalidateVisual;
        DisplayManager.Changed += () => { UpdateFont(); OnSize(); InvalidateVisual(); };
        Unloaded += (_, _) => _blink.Stop();
    }

    public static string? Executable(ShellKind kind) => kind switch
    {
        ShellKind.PowerShell => "powershell.exe",
        ShellKind.Pwsh => FindOnPath("pwsh.exe") ?? new[] { @"C:\Program Files\PowerShell\7\pwsh.exe" }.FirstOrDefault(File.Exists),
        ShellKind.Cmd => "cmd.exe",
        ShellKind.GitBash => ConsoleView.GitBashPath,
        ShellKind.Wsl => File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")) ? Path.Combine(Environment.SystemDirectory, "wsl.exe") : null,
        _ => null,
    };

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Length > 0)
            .Select(d => { try { return Path.Combine(d.Trim(), exe); } catch { return null; } })
            .FirstOrDefault(p => p != null && File.Exists(p));

    private string CommandLine() => Shell switch
    {
        ShellKind.PowerShell => "powershell.exe -NoLogo",
        ShellKind.Pwsh => $"\"{Executable(ShellKind.Pwsh)}\" -NoLogo",
        ShellKind.Cmd => "cmd.exe",
        ShellKind.GitBash => $"\"{Executable(ShellKind.GitBash)}\" --login -i",
        ShellKind.Wsl => "wsl.exe",
        _ => "cmd.exe",
    };

    // ------------------------------------------------------------------ process

    private void Start()
    {
        _exited = false;
        var env = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "NeoIDE",
        };
        // the project PHP first on PATH: "php", "composer", "bin/neo" use the interpreter of the project
        if (Workspace.PhpExe != null && Path.GetDirectoryName(Workspace.PhpExe) is { } phpDir)
            env["PATH"] = phpDir + ";" + Environment.GetEnvironmentVariable("PATH");
        if (Shell == ShellKind.GitBash) env["CHERE_INVOKING"] = "1"; // stay in the working directory
        try
        {
            var (cols, rows) = (_screen.Cols, _screen.Rows);
            _pty = new PseudoConsole(CommandLine(), Directory.Exists(WorkingDirectory) ? WorkingDirectory : Environment.CurrentDirectory, (short)cols, (short)rows, env);
            var pty = _pty;
            pty.Output += data => Enqueue(data);
            pty.Exited += code => Dispatcher.BeginInvoke(() =>
            {
                if (_pty != pty) return;
                _exited = true;
                Flush();
                _screen.Feed($"\r\n\x1b[90m[process exited with code {code} — press Enter to restart]\x1b[0m\r\n");
                InvalidateVisual();
            });
            _screen.Reply += s => _pty?.Write(s);
        }
        catch (Exception ex)
        {
            _screen.Feed($"\x1b[31mCannot start the terminal: {ex.Message}\x1b[0m\r\n");
            _exited = true;
        }
    }

    public void Restart()
    {
        Stop();
        _screen = new VtScreen(_screen.Cols, _screen.Rows);
        HookScreen();
        _scroll = 0;
        ClearSelection();
        Start();
        InvalidateVisual();
    }

    public void Stop()
    {
        var p = _pty;
        _pty = null;
        if (p != null) System.Threading.Tasks.Task.Run(p.Dispose);
    }

    private void HookScreen()
    {
        _screen.TitleChanged += () => Dispatcher.BeginInvoke(() => TitleChanged?.Invoke());
        _screen.LineScrolledOff += () => { if (_scroll > 0) _scroll = Math.Min(_scroll + 1, _screen.Scrollback.Count); };
    }

    /// <summary>Sends text to the shell as if it was typed (e.g. "php bin/neo cache:clear\r").</summary>
    public void SendText(string text)
    {
        if (_exited) return;
        _scroll = 0;
        _pty?.Write(text);
    }

    public void Clear()
    {
        _screen.ClearScrollback();
        _scroll = 0;
        // Ctrl+L: bash / PSReadLine clear the screen and redraw the prompt; cmd has no such key: clear our side only
        if (Shell == ShellKind.Cmd || _exited) _screen.Feed("\x1b[2J\x1b[H");
        else _pty?.Write("\x0c");
        InvalidateVisual();
    }

    private void Enqueue(string data)
    {
        lock (_pendingGate)
        {
            _pending.Append(data);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        Dispatcher.BeginInvoke(Flush, DispatcherPriority.Render);
    }

    private void Flush()
    {
        string data;
        lock (_pendingGate)
        {
            data = _pending.ToString();
            _pending.Clear();
            _flushQueued = false;
        }
        if (data.Length == 0) return;
        _screen.Feed(data);
        _blinkOn = true;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ layout

    private void UpdateFont()
    {
        var family = DisplayManager.EditorFont;
        _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, FontSize, Brushes.Black, 1.0);
        _cellW = ft.WidthIncludingTrailingWhitespace / 10;
        _cellH = Math.Ceiling(ft.Height + 1);
        _baseline = ft.Baseline;
    }

    private static double FontSize => Math.Max(10, AppSettings.Current.EditorFontSize - 0.5);

    private void OnSize()
    {
        if (ActualWidth < 20 || ActualHeight < 20) return;
        var cols = Math.Max(20, (int)((ActualWidth - PadX * 2) / _cellW));
        var rows = Math.Max(4, (int)((ActualHeight - PadY * 2) / _cellH));
        if (_pty == null && !_exited)
        {
            _screen = new VtScreen(cols, rows);
            HookScreen();
            Start();
            return;
        }
        if (cols == _screen.Cols && rows == _screen.Rows) return;
        _screen.Resize(cols, rows);
        _pty?.Resize((short)cols, (short)rows);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ colours

    private static readonly string[] DarkPalette =
    {
        "#1E1F22", "#F75464", "#5FB865", "#E5B54B", "#548AF7", "#C77DBB", "#2FB8B5", "#BCBEC4",
        "#6F737A", "#FF7A85", "#7FD48A", "#F2CB6C", "#7AA7FF", "#DFA0D7", "#56D4D0", "#FFFFFF",
    };
    private static readonly string[] LightPalette =
    {
        "#000000", "#C7222D", "#2A7F33", "#9E7400", "#2D5FCC", "#8E3E9E", "#167F7C", "#6F737A",
        "#8C8C8C", "#E5484D", "#3D9A47", "#B8860B", "#3574F0", "#A24DB4", "#1D9C98", "#1E1F22",
    };

    private readonly Dictionary<int, SolidColorBrush> _brushes = new();

    private Color ColorOf(int c, bool foreground)
    {
        if (c < 0) return ((SolidColorBrush)ThemeManager.Brush(foreground ? "Fg" : "ConsoleBg")).Color;
        if ((c & 0x1000000) != 0) return Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        if (c < 16) return (Color)ColorConverter.ConvertFromString((ThemeManager.IsDark ? DarkPalette : LightPalette)[c]);
        if (c < 232)
        {
            var i = c - 16;
            byte L(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromRgb(L(i / 36), L(i / 6 % 6), L(i % 6));
        }
        var g = (byte)(8 + (c - 232) * 10);
        return Color.FromRgb(g, g, g);
    }

    private Brush BrushOf(int c, bool foreground)
    {
        var key = c < 0 ? (foreground ? -2 : -3) : c;
        var color = ColorOf(c, foreground);
        if (_brushes.TryGetValue(key, out var b) && b.Color == color) return b;
        b = new SolidColorBrush(color);
        b.Freeze();
        _brushes[key] = b;
        return b;
    }

    // ------------------------------------------------------------------ rendering

    private int TotalRows => _screen.Scrollback.Count + _screen.Rows;
    private int FirstVisibleRow => _screen.Scrollback.Count - _scroll;

    private Cell[] RowAt(int absolute) =>
        absolute < _screen.Scrollback.Count ? _screen.Scrollback[absolute] : _screen.Line(Math.Clamp(absolute - _screen.Scrollback.Count, 0, _screen.Rows - 1));

    protected override void OnRender(DrawingContext dc)
    {
        var bg = BrushOf(-1, false);
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var selBrush = new SolidColorBrush(Editor.EditorPalette.Selection);
        var first = FirstVisibleRow;
        var sel = NormalizedSelection();
        for (int r = 0; r < _screen.Rows; r++)
        {
            var abs = first + r;
            if (abs < 0 || abs >= TotalRows) continue;
            var line = RowAt(abs);
            var y = PadY + r * _cellH;
            int x = 0;
            while (x < line.Length)
            {
                var c = line[x];
                int start = x;
                var sb = new StringBuilder();
                while (x < line.Length && Same(line[x], c))
                {
                    sb.Append(line[x].Ch == '\0' ? ' ' : line[x].Ch);
                    if (line[x].Low != '\0') sb.Append(line[x].Low);
                    x++;
                }
                var (fgc, bgc) = (c.Attr & CellAttr.Inverse) != 0 ? (c.Bg, c.Fg) : (c.Fg, c.Bg);
                var rect = new Rect(PadX + start * _cellW, y, (x - start) * _cellW, _cellH);
                if (bgc >= 0 || (c.Attr & CellAttr.Inverse) != 0) dc.DrawRectangle(BrushOf(bgc, (c.Attr & CellAttr.Inverse) != 0 && bgc < 0), null, rect);
                var text = sb.ToString();
                if (text.Trim().Length == 0 && (c.Attr & CellAttr.Underline) == 0) continue;
                Brush fg = BrushOf(fgc < 8 && fgc >= 0 && (c.Attr & CellAttr.Bold) != 0 ? fgc + 8 : fgc, (c.Attr & CellAttr.Inverse) == 0 || fgc >= 0);
                if ((c.Attr & CellAttr.Inverse) != 0 && fgc < 0) fg = BrushOf(-1, false);
                if ((c.Attr & CellAttr.Dim) != 0) { fg = fg.Clone(); fg.Opacity = 0.6; }
                var face = (c.Attr & CellAttr.Bold) != 0 ? _bold : (c.Attr & CellAttr.Italic) != 0 ? _italic : _regular;
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize, fg, dpi);
                if ((c.Attr & CellAttr.Underline) != 0) ft.SetTextDecorations(TextDecorations.Underline);
                if ((c.Attr & CellAttr.Strike) != 0) ft.SetTextDecorations(TextDecorations.Strikethrough);
                dc.DrawText(ft, new Point(rect.X, y + (_cellH - ft.Height) / 2));
            }
            // selection
            if (sel is { } s && abs >= s.a.row && abs <= s.b.row)
            {
                var from = abs == s.a.row ? s.a.col : 0;
                var to = abs == s.b.row ? s.b.col : _screen.Cols;
                if (to > from) dc.DrawRectangle(selBrush, null, new Rect(PadX + from * _cellW, y, (to - from) * _cellW, _cellH));
            }
        }
        // cursor (only when the bottom of the screen is visible)
        if (_scroll == 0 && _screen.CursorVisible && !_exited)
        {
            var rect = new Rect(PadX + _screen.CursorX * _cellW, PadY + _screen.CursorY * _cellH, _cellW, _cellH);
            var caret = new SolidColorBrush(Editor.EditorPalette.Caret);
            if (IsKeyboardFocused)
            {
                if (_blinkOn)
                {
                    dc.DrawRectangle(caret, null, rect);
                    var cell = _screen.Line(_screen.CursorY)[_screen.CursorX];
                    if (cell.Ch is not (' ' or '\0'))
                        dc.DrawText(new FormattedText(cell.Ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, FontSize, bg, dpi), rect.TopLeft);
                }
            }
            else dc.DrawRectangle(null, new Pen(caret, 1), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
        }
        // scrolled back: a hint at the bottom right
        if (_scroll > 0)
        {
            var hint = new FormattedText($"↑ {_scroll} line(s) — type or press Shift+End to go back", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, 11, ThemeManager.Brush("FgSecondary"), dpi);
            dc.DrawText(hint, new Point(ActualWidth - hint.Width - 12, ActualHeight - hint.Height - 4));
        }
    }

    private static bool Same(Cell a, Cell b) => a.Fg == b.Fg && a.Bg == b.Bg && a.Attr == b.Attr;

    // ------------------------------------------------------------------ keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (KeyboardHelper.IsAltGr) return; // characters typed with AltGr come through TextInput

        if (_exited && key == Key.Enter) { Restart(); e.Handled = true; return; }

        // copy / paste
        if (mods == ModifierKeys.Control && key == Key.C && HasSelection) { CopySelection(); e.Handled = true; return; }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && key == Key.V || mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.V || mods == ModifierKeys.Shift && key == Key.Insert)
        {
            Paste();
            e.Handled = true;
            return;
        }
        // scrollback
        if (mods == ModifierKeys.Shift && key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            _scroll = key switch
            {
                Key.PageUp => Math.Min(_screen.Scrollback.Count, _scroll + _screen.Rows - 1),
                Key.PageDown => Math.Max(0, _scroll - _screen.Rows + 1),
                Key.Home => _screen.Scrollback.Count,
                _ => 0,
            };
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var seq = KeySequence(key, mods);
        if (seq != null)
        {
            ClearSelection();
            SendText(seq);
            e.Handled = true;
        }
    }

    private string? KeySequence(Key key, ModifierKeys mods)
    {
        bool ctrl = (mods & ModifierKeys.Control) != 0, alt = (mods & ModifierKeys.Alt) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        int m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Arrow(char c) => m > 1 ? $"\x1b[1;{m}{c}" : _screen.AppCursorKeys ? $"\x1bO{c}" : $"\x1b[{c}";
        string Tilde(int n) => m > 1 ? $"\x1b[{n};{m}~" : $"\x1b[{n}~";
        switch (key)
        {
            case Key.Enter: return alt ? "\x1b\r" : "\r";
            case Key.Back: return ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f";
            case Key.Tab: return shift ? "\x1b[Z" : "\t";
            case Key.Escape: return "\x1b";
            case Key.Up: return Arrow('A');
            case Key.Down: return Arrow('B');
            case Key.Right: return Arrow('C');
            case Key.Left: return Arrow('D');
            case Key.Home: return Arrow('H');
            case Key.End: return Arrow('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case >= Key.F1 and <= Key.F4: return m > 1 ? $"\x1b[1;{m}{"PQRS"[key - Key.F1]}" : $"\x1bO{"PQRS"[key - Key.F1]}";
            case >= Key.F5 and <= Key.F12: return Tilde(new[] { 15, 17, 18, 19, 20, 21, 23, 24 }[key - Key.F5]);
        }
        if (ctrl && !alt)
        {
            if (key is >= Key.A and <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
            if (key == Key.Space || key == Key.D2) return "\0";
            if (key == Key.Oem4) return "\x1b";      // Ctrl+[
            if (key == Key.Oem6) return "\x1d";      // Ctrl+]
            if (key == Key.Oem5) return "\x1c";      // Ctrl+\
        }
        if (alt && !ctrl && key is >= Key.A and <= Key.Z)
            return "\x1b" + (char)((shift ? 'A' : 'a') + (key - Key.A));
        return null;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.All(char.IsControl)) return; // handled by OnPreviewKeyDown
        ClearSelection();
        SendText(e.Text);
        e.Handled = true;
    }

    private void Paste()
    {
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
        if (_screen.BracketedPaste) text = "\x1b[200~" + text + "\x1b[201~";
        SendText(text);
    }

    // ------------------------------------------------------------------ mouse

    private (int row, int col) CellAt(Point p)
    {
        var col = Math.Clamp((int)Math.Round((p.X - PadX) / _cellW), 0, _screen.Cols);
        var row = Math.Clamp((int)((p.Y - PadY) / _cellH), 0, _screen.Rows - 1);
        return (FirstVisibleRow + row, col);
    }

    private bool HasSelection => _selStart != null && _selEnd != null && _selStart != _selEnd;

    private ((int row, int col) a, (int row, int col) b)? NormalizedSelection()
    {
        if (!HasSelection) return null;
        var (a, b) = (_selStart!.Value, _selEnd!.Value);
        if (b.row < a.row || b.row == a.row && b.col < a.col) (a, b) = (b, a);
        return (a, b);
    }

    private void ClearSelection()
    {
        if (_selStart == null) return;
        _selStart = _selEnd = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var cell = CellAt(e.GetPosition(this));
        if (e.ClickCount == 2)
        {
            // word selection
            var line = RowAt(cell.row);
            int s = Math.Min(cell.col, line.Length - 1), en = s;
            bool Word(int i) => i >= 0 && i < line.Length && !char.IsWhiteSpace(line[i].Ch) && line[i].Ch != '\0' && "\"'()[]{}<>|,;".IndexOf(line[i].Ch) < 0;
            while (Word(s - 1)) s--;
            while (Word(en)) en++;
            _selStart = (cell.row, s);
            _selEnd = (cell.row, en);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _selStart = _selEnd = cell;
        _selecting = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_selecting) { Cursor = Keyboard.Modifiers == ModifierKeys.Control && LinkAt(e.GetPosition(this)) != null ? Cursors.Hand : Cursors.IBeam; return; }
        var p = e.GetPosition(this);
        if (p.Y < 0 && _scroll < _screen.Scrollback.Count) _scroll++;
        else if (p.Y > ActualHeight && _scroll > 0) _scroll--;
        _selEnd = CellAt(p);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        if (!HasSelection && Keyboard.Modifiers == ModifierKeys.Control && LinkAt(e.GetPosition(this)) is { } link)
        {
            ClearSelection();
            LinkClicked?.Invoke(link, CurrentDirectoryGuess());
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        // Windows Terminal behaviour: copy the selection, or paste
        if (HasSelection) CopySelection(); else Paste();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var lines = e.Delta > 0 ? 3 : -3;
        _scroll = Math.Clamp(_scroll + lines, 0, _screen.Scrollback.Count);
        InvalidateVisual();
        e.Handled = true;
    }

    private void CopySelection()
    {
        if (NormalizedSelection() is not { } s) return;
        var sb = new StringBuilder();
        for (int r = s.a.row; r <= s.b.row; r++)
        {
            var line = RowAt(r);
            var from = r == s.a.row ? s.a.col : 0;
            var to = r == s.b.row ? s.b.col : line.Length;
            var part = new StringBuilder();
            for (int x = from; x < Math.Min(to, line.Length); x++) { part.Append(line[x].Ch == '\0' ? ' ' : line[x].Ch); if (line[x].Low != '\0') part.Append(line[x].Low); }
            sb.Append(part.ToString().TrimEnd());
            if (r < s.b.row) sb.Append(Environment.NewLine);
        }
        try { Clipboard.SetText(sb.ToString()); } catch { }
        ClearSelection();
    }

    private static readonly Regex LinkPattern = new(
        @"https?://[^\s'""<>]+|(?:[A-Za-z]:)?[\w.\-\\/]*[\w\-]\.(?:php|twig|ya?ml|json|js|ts|css|scss|md|env|xml|neon|lock|txt|log|html)(?:(?::|\()\d+\)?)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string? LinkAt(Point p)
    {
        var (row, col) = CellAt(p);
        var text = VtScreen.TextOf(RowAt(row));
        foreach (Match m in LinkPattern.Matches(text))
            if (col >= m.Index && col <= m.Index + m.Length) return m.Value.TrimEnd('.', ',', ')', ':');
        return null;
    }

    /// <summary>The prompt usually shows the directory ("PS C:\x>", "user@host MINGW64 /c/x"): else the start directory.</summary>
    private string CurrentDirectoryGuess()
    {
        for (int r = TotalRows - 1; r >= Math.Max(0, TotalRows - 200); r--)
        {
            var text = VtScreen.TextOf(RowAt(r));
            var ps = Regex.Match(text, @"^PS ([A-Za-z]:\\[^>]*)>");
            if (ps.Success && Directory.Exists(ps.Groups[1].Value)) return ps.Groups[1].Value;
            var cmd = Regex.Match(text, @"^([A-Za-z]:\\[^>]*)>");
            if (cmd.Success && Directory.Exists(cmd.Groups[1].Value)) return cmd.Groups[1].Value;
        }
        return WorkingDirectory;
    }
}
