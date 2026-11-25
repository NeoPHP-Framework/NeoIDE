using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Services;

namespace NeoIDE.Controls;

public enum ConsoleKind { Output, Neo, PowerShell, Cmd, Bash }

/// <summary>
/// Output console (Run tool window) or a terminal: NeoConsole (bin/neo commands with completion),
/// PowerShell, Command Prompt or Git Bash. Each command runs in its own process; the current
/// directory is tracked between commands.
/// </summary>
public sealed class ConsoleView : DockPanel
{
    private readonly TextEditor _output;
    private readonly TextBox _input;
    private readonly TextBlock _prompt;
    private readonly DockPanel _inputRow;
    private readonly LineColorizer _colorizer = new();
    private readonly Popup _suggest;
    private readonly ListBox _suggestList;
    private ProcessRunner? _runner;
    private readonly List<string> _history = new();
    private int _historyIndex;
    private static readonly Dictionary<string, List<(string, string)>> OptionsCache = new();
    private const string CwdMarker = "__NEOIDE_CWD__";

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(ConsoleView), new PropertyMetadata(false, (d, _) => ((ConsoleView)d).UpdateInteractive()));

    public bool Interactive { get => (bool)GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }

    private ConsoleKind _kind = ConsoleKind.Output;
    public ConsoleKind Kind
    {
        get => _kind;
        set { _kind = value; Interactive = value != ConsoleKind.Output; UpdatePrompt(); }
    }

    private string _cwd = Environment.CurrentDirectory;
    public string WorkingDirectory { get => _cwd; set { _cwd = value; UpdatePrompt(); } }
    public bool IsRunning => _runner?.IsRunning == true;
    public event Action<bool>? RunningChanged;

    public static string? GitBashPath =>
        new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe") }
            .FirstOrDefault(File.Exists);

    public ConsoleView()
    {
        _output = new TextEditor
        {
            IsReadOnly = true,
            FontFamily = DisplayManager.EditorFont,
            FontSize = 12.5,
            ShowLineNumbers = false,
            WordWrap = true,
            Padding = new Thickness(10, 4, 4, 4),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _output.Options.EnableHyperlinks = true;
        _output.Options.RequireControlModifierForHyperlinkClick = false;
        _output.TextArea.TextView.LineTransformers.Add(_colorizer);

        _prompt = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), FontFamily = _output.FontFamily, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        _input = new TextBox
        {
            FontFamily = _output.FontFamily, FontSize = 12.5, BorderThickness = new Thickness(0), Padding = new Thickness(0, 7, 0, 7),
            Background = Brushes.Transparent,
        };
        // a plain TextBox look: no focus border from the global style
        _input.Template = BuildPlainTemplate();
        _input.PreviewKeyDown += OnInputKeyDown;
        _input.TextChanged += (_, _) => UpdateSuggestions();
        _input.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (!_suggestList!.IsKeyboardFocusWithin) _suggest!.IsOpen = false; });

        _inputRow = new DockPanel { Visibility = Visibility.Collapsed };
        DockPanel.SetDock(_prompt, Dock.Left);
        _inputRow.Children.Add(_prompt);
        _inputRow.Children.Add(_input);
        var inputBorder = new Border { Child = _inputRow, BorderThickness = new Thickness(0, 1, 0, 0), Tag = "inputBorder" };
        SetDock(inputBorder, Dock.Bottom);
        Children.Add(inputBorder);
        Children.Add(_output);

        _suggestList = new ListBox { MaxHeight = 260, MinWidth = 460, Padding = new Thickness(2) };
        _suggestList.MouseDoubleClick += (_, _) => AcceptSuggestion();
        _suggest = new Popup
        {
            // placed by hand above the caret: PlacementMode.Top follows the Windows "menu alignment" (right-handed)
            // setting and could open the list at the far right of the console
            PlacementTarget = _input, Placement = PlacementMode.Custom, AllowsTransparency = true, StaysOpen = false,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                var caret = _input!.GetRectFromCharacterIndex(Math.Min(_input.CaretIndex, _input.Text.Length));
                // the callback works in device pixels, the caret rectangle in WPF units
                var dpi = VisualTreeHelper.GetDpi(_input).DpiScaleX;
                var x = double.IsInfinity(caret.X) || double.IsNaN(caret.X) ? 0 : Math.Max(0, (caret.X - 12) * dpi);
                x = Math.Min(x, Math.Max(0, targetSize.Width - popupSize.Width));
                return new[] { new CustomPopupPlacement(new Point(x, -popupSize.Height), PopupPrimaryAxis.Horizontal) };
            },
            Child = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = _suggestList, Margin = new Thickness(0, 0, 0, 4) },
        };

        ThemeManager.ThemeChanged += ApplyTheme;
        DisplayManager.Changed += () => { _output.FontFamily = _input.FontFamily = _prompt.FontFamily = DisplayManager.EditorFont; };
        ApplyTheme();
    }

    private static ControlTemplate BuildPlainTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        t.VisualTree = sv;
        return t;
    }

    private void UpdateInteractive()
    {
        if (_inputRow.Parent is Border b) b.Visibility = Interactive ? Visibility.Visible : Visibility.Collapsed;
        _inputRow.Visibility = Interactive ? Visibility.Visible : Visibility.Collapsed;
        UpdatePrompt();
    }

    private void UpdatePrompt()
    {
        if (_prompt == null) return;
        var dir = Path.GetFileName(_cwd.TrimEnd('\\'));
        _prompt.Text = _kind switch
        {
            ConsoleKind.Neo => "neo ›",
            ConsoleKind.PowerShell => $"PS {dir}>",
            ConsoleKind.Cmd => $"{dir}>",
            ConsoleKind.Bash => $"{dir} $",
            _ => $"{dir} ›",
        };
        _prompt.ToolTip = _cwd;
    }

    private void ApplyTheme()
    {
        _output.Background = ThemeManager.Brush("ConsoleBg");
        _output.Foreground = ThemeManager.Brush("Fg");
        _output.TextArea.SelectionBrush = new SolidColorBrush(Editor.EditorPalette.Selection);
        _output.TextArea.SelectionForeground = null;
        _output.TextArea.TextView.LinkTextForegroundBrush = ThemeManager.Brush("LinkFg");
        _prompt.Foreground = ThemeManager.Brush(_kind == ConsoleKind.Neo ? "Accent" : "SuccessFg");
        _input.Foreground = ThemeManager.Brush("Fg");
        _input.CaretBrush = ThemeManager.Brush("Fg");
        if (_inputRow.Parent is Border b) { b.BorderBrush = ThemeManager.Brush("SubtleBorder"); b.Background = ThemeManager.Brush("ConsoleBg"); }
        if (_suggest.Child is Border sb) { sb.Background = ThemeManager.Brush("PopupBg"); sb.BorderBrush = ThemeManager.Brush("PopupBorder"); }
        _colorizer.ErrorBrush = ThemeManager.Brush("ErrorFg");
        _colorizer.WarnBrush = ThemeManager.Brush("WarnFg");
        _colorizer.InfoBrush = ThemeManager.Brush("FgSecondary");
        _colorizer.OkBrush = ThemeManager.Brush("SuccessFg");
        _colorizer.CommandBrush = ThemeManager.Brush("Accent");
        _output.TextArea.TextView.Redraw();
    }

    public void FocusInput() => _input.Focus();

    public void Clear()
    {
        _output.Document.Text = "";
        _colorizer.Lines.Clear();
    }

    // ------------------------------------------------------------------ output

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>Real errors only: PHP errors, exceptions, [ERROR] blocks, git "fatal:"... — not every line containing "failed".</summary>
    private static readonly Regex ErrorLine = new(
        @"^\s*(PHP\s+)?(Fatal error|Parse error|Uncaught|Error:|error:|fatal:|ERROR\b|\[ERROR\]|\[CRITICAL\]|In .+ line \d+:|\S*Exception\b.*:|\[[\w\\]*Exception\])" +
        @"|^\s*!!\s|Your requirements could not be resolved|Could not open input file",
        RegexOptions.Compiled);

    private static readonly Regex WarnLine = new(@"^\s*(PHP\s+)?(Warning|Deprecated|Notice):|^\s*\[WARNING\]|^\s*warning:", RegexOptions.Compiled);
    private static readonly Regex OkLine = new(@"^\s*\[OK\]|✔", RegexOptions.Compiled);

    public enum LineKind { Normal, Error, Warning, Info, Ok, Command }

    public void Append(string text, bool isError = false, bool isInfo = false) =>
        AppendKind(text, isError ? LineKind.Error : isInfo ? LineKind.Info : LineKind.Normal);

    public void AppendKind(string text, LineKind kind)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendKind(text, kind)); return; }
        text = Ansi.Replace(text, "");
        var doc = _output.Document;
        bool startsLine = doc.TextLength == 0 || doc.GetCharAt(doc.TextLength - 1) == '\n';
        int firstLine = startsLine ? doc.LineCount : doc.LineCount + 1;
        doc.Insert(doc.TextLength, (startsLine ? "" : "\n") + text);
        _lineOpen = true; // a line written here has no new line: the raw output of a command starts below it
        for (int l = firstLine; l <= doc.LineCount; l++)
        {
            var lineText = doc.GetText(doc.GetLineByNumber(l));
            var k = kind;
            if (k == LineKind.Normal)
            {
                if (ErrorLine.IsMatch(lineText)) k = LineKind.Error;
                else if (WarnLine.IsMatch(lineText)) k = LineKind.Warning;
                else if (OkLine.IsMatch(lineText)) k = LineKind.Ok;
            }
            if (k != LineKind.Normal) _colorizer.Lines[l] = k;
        }
        _output.ScrollToEnd();
    }

    private bool _lineOpen;

    /// <summary>A complete output line of the running command (used to detect generated files).</summary>
    public event Action<string>? LineReceived;

    /// <summary>Raw output of an interactive command: written as it arrives (prompts without new line included).</summary>
    private void AppendRaw(string chunk, bool stderr, bool isAnswer = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendRaw(chunk, stderr, isAnswer)); return; }
        chunk = Ansi.Replace(chunk, "").Replace("\r\n", "\n").Replace("\r", "");
        if (chunk.Length == 0) return;
        var doc = _output.Document;
        if (_lineOpen && !isAnswer && doc.TextLength > 0 && doc.GetCharAt(doc.TextLength - 1) != '\n') chunk = "\n" + chunk;
        _lineOpen = false;
        int firstLine = doc.LineCount;
        doc.Insert(doc.TextLength, chunk);
        int lastComplete = doc.GetCharAt(doc.TextLength - 1) == '\n' ? doc.LineCount - 1 : doc.LineCount - 1;
        for (int l = firstLine; l <= doc.LineCount; l++)
        {
            var line = doc.GetText(doc.GetLineByNumber(l));
            if (isAnswer) { if (l == firstLine) _colorizer.Lines[l] = LineKind.Command; continue; }
            var kind = ErrorLine.IsMatch(line) ? LineKind.Error : WarnLine.IsMatch(line) ? LineKind.Warning : OkLine.IsMatch(line) ? LineKind.Ok :
                       Regex.IsMatch(line, @"^\s*\S.*(\?|:)\s*(\[[^\]]*\])?:?\s*$|^ > ") ? LineKind.Command : LineKind.Normal;
            if (kind != LineKind.Normal) _colorizer.Lines[l] = kind; else _colorizer.Lines.Remove(l);
            if (l <= lastComplete && l < doc.LineCount) LineReceived?.Invoke(line);
        }
        _output.ScrollToEnd();
    }

    /// <summary>Process output: stderr is shown in red only when it really is an error (composer/git write progress on stderr).</summary>
    private void OnProcessOutput(string line, bool stderr)
    {
        if (line.StartsWith(CwdMarker))
        {
            var dir = line[CwdMarker.Length..].Trim();
            if (dir.Length > 0 && Directory.Exists(dir)) Dispatcher.BeginInvoke(() => WorkingDirectory = Path.GetFullPath(dir));
            return;
        }
        AppendKind(line, stderr && ErrorLine.IsMatch(line) ? LineKind.Error : LineKind.Normal);
    }

    /// <summary>Runs a process and streams its output here (Run tool window).</summary>
    public async Task<int> RunAsync(string exe, string args, string? cwd = null, string? title = null, IDictionary<string, string>? env = null)
    {
        Stop();
        _runner = new ProcessRunner();
        _runner.Output += OnProcessOutput;
        AppendKind(title ?? $"{Path.GetFileName(exe)} {args}", LineKind.Command);
        RunningChanged?.Invoke(true);
        var code = await _runner.StartAsync(exe, args, cwd ?? WorkingDirectory, env);
        AppendKind($"Process finished with exit code {code}", code == 0 ? LineKind.Info : LineKind.Error);
        RunningChanged?.Invoke(false);
        return code;
    }

    public void Stop()
    {
        if (_runner?.IsRunning == true)
        {
            _runner.Stop();
            AppendKind("Process stopped", LineKind.Info);
        }
    }

    // ------------------------------------------------------------------ input

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (KeyboardHelper.IsAltGr) return; // AltGr types @ { } [ ] \ | ...
        if (_suggest.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down: _suggestList.SelectedIndex = Math.Min(_suggestList.Items.Count - 1, _suggestList.SelectedIndex + 1); _suggestList.ScrollIntoView(_suggestList.SelectedItem); e.Handled = true; return;
                case Key.Up: _suggestList.SelectedIndex = Math.Max(0, _suggestList.SelectedIndex - 1); _suggestList.ScrollIntoView(_suggestList.SelectedItem); e.Handled = true; return;
                case Key.Tab: AcceptSuggestion(); e.Handled = true; return;
                case Key.Escape: _suggest.IsOpen = false; e.Handled = true; return;
                case Key.Enter when _suggestList.SelectedItem is Suggestion s && !_input.Text.TrimEnd().EndsWith(s.Text):
                    AcceptSuggestion(); e.Handled = true; return;
            }
        }
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { UpdateSuggestions(force: true); e.Handled = true; return; }
        if (e.Key == Key.Tab && _kind == ConsoleKind.Neo) { UpdateSuggestions(force: true); e.Handled = true; return; }
        if (e.Key == Key.Up && _history.Count > 0)
        {
            _historyIndex = Math.Max(0, _historyIndex - 1);
            SetInput(_history[_historyIndex]);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down && _history.Count > 0)
        {
            _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            SetInput(_historyIndex < _history.Count ? _history[_historyIndex] : "");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _input.SelectionLength == 0) { Stop(); e.Handled = true; return; }
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { Clear(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _suggest.IsOpen = false;
        // a command is waiting for an answer: send what was typed (an empty line keeps the default value)
        if (IsRunning && _interactive)
        {
            var answer = _input.Text;
            _input.Text = "";
            AppendRaw(answer + "\n", false, isAnswer: true);
            _runner!.SendLine(answer);
            return;
        }
        var cmd = _input.Text.Trim();
        _input.Text = "";
        if (cmd.Length == 0) return;
        if (IsRunning) { AppendKind("A command is already running (Ctrl+C to stop it).", LineKind.Warning); _input.Text = cmd; return; }
        _history.Remove(cmd);
        _history.Add(cmd);
        _historyIndex = _history.Count;
        await Execute(cmd);
    }

    private void SetInput(string text)
    {
        _input.Text = text;
        _input.CaretIndex = text.Length;
        _suggest.IsOpen = false;
    }

    private bool _interactive;

    public async Task<int> Execute(string cmd)
    {
        AppendKind($"{_prompt.Text} {cmd}", LineKind.Command);
        if (cmd is "cls" or "clear") { Clear(); return 0; }

        string exe, args;
        string? stdin = null;
        var cwd = _cwd;
        switch (_kind)
        {
            case ConsoleKind.Neo:
            {
                if (Workspace.PhpExe == null || Workspace.Root == null) { AppendKind("No PHP interpreter / project.", LineKind.Error); return -1; }
                var neoArgs = Regex.Replace(cmd, @"^(php\s+)?(bin/neo|neo)\s*", "");
                // interactive: NeoPHP asks its questions (make:entity, make:controller...) and reads the answers on stdin
                _runner = new ProcessRunner();
                _runner.RawOutput += (chunk, err) => AppendRaw(chunk, err);
                _interactive = true;
                RunningChanged?.Invoke(true);
                _input.Focus();
                var code = await _runner.StartInteractiveAsync(Workspace.PhpExe, $"{Workspace.NeoScript} {neoArgs} --no-ansi", Workspace.Root);
                _interactive = false;
                if (code != 0) AppendKind($"exit code {code}", LineKind.Info);
                RunningChanged?.Invoke(false);
                return code;
            }
            case ConsoleKind.PowerShell:
                exe = "powershell.exe";
                args = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -";
                stdin = $"$ProgressPreference='SilentlyContinue'\n{cmd}\n\"{CwdMarker}$((Get-Location).Path)\"\nexit\n";
                break;
            case ConsoleKind.Bash:
                exe = GitBashPath ?? "bash.exe";
                args = "-l -s";
                stdin = $"{cmd}\necho \"{CwdMarker}$(pwd -W 2>/dev/null || pwd)\"\n";
                break;
            default:
                exe = "cmd.exe";
                args = $"/d /s /c \"{cmd} & echo {CwdMarker}%__CD__%\"";
                // %__CD__% is evaluated at parse time: handle "cd" ourselves for cmd
                var m = Regex.Match(cmd, @"^cd(\s+/d)?\s+(?<dir>.+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var target = Path.GetFullPath(Path.Combine(_cwd, m.Groups["dir"].Value.Trim().Trim('"')));
                    if (Directory.Exists(target)) WorkingDirectory = target;
                    else AppendKind("The system cannot find the path specified.", LineKind.Error);
                    return 0;
                }
                break;
        }

        _runner = new ProcessRunner();
        _runner.Output += OnProcessOutput;
        RunningChanged?.Invoke(true);
        var exit = await _runner.StartAsync(exe, args, cwd, stdin: stdin);
        if (exit != 0 && _kind != ConsoleKind.Output) AppendKind($"exit code {exit}", LineKind.Info);
        RunningChanged?.Invoke(false);
        return exit;
    }

    // ------------------------------------------------------------------ NeoConsole completion

    private sealed record Suggestion(string Text, string Description, bool IsOption)
    {
        public override string ToString() => Text;
    }

    private void UpdateSuggestions(bool force = false)
    {
        if (_kind != ConsoleKind.Neo) { _suggest.IsOpen = false; return; }
        var text = _input.Text;
        var items = new List<Suggestion>();
        var parts = text.TrimStart().Split(' ', 2);
        if (parts.Length == 1)
        {
            var prefix = parts[0];
            if (prefix.Length == 0 && !force) { _suggest.IsOpen = false; return; }
            items = Workspace.NeoCommands
                .Where(c => c.name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.name)
                .Select(c => new Suggestion(c.name, c.description, false)).ToList();
        }
        else
        {
            // options of the command (from "bin/neo <command> --help")
            var command = parts[0];
            var lastWord = text.EndsWith(" ") ? "" : text.Split(' ').Last();
            if (!lastWord.StartsWith("-") && !force) { _suggest.IsOpen = false; return; }
            if (!OptionsCache.TryGetValue(command, out var options)) { _ = LoadOptionsAsync(command); return; }
            items = options.Where(o => o.Item1.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase) && !text.Contains(" " + o.Item1))
                .Select(o => new Suggestion(o.Item1, o.Item2, true)).ToList();
        }
        if (items.Count == 0 || (items.Count == 1 && items[0].Text == parts.Last().Trim())) { _suggest.IsOpen = false; return; }
        _suggestList.ItemsSource = items;
        _suggestList.ItemTemplate ??= BuildSuggestionTemplate();
        _suggestList.SelectedIndex = 0;
        _suggest.IsOpen = true;
    }

    private static DataTemplate BuildSuggestionTemplate()
    {
        var t = new DataTemplate(typeof(Suggestion));
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));
        name.SetValue(TextBlock.FontFamilyProperty, DisplayManager.EditorFont);
        name.SetValue(FrameworkElement.MinWidthProperty, 190.0);
        name.SetValue(DockPanel.DockProperty, Dock.Left);
        dock.AppendChild(name);
        var desc = new FrameworkElementFactory(typeof(TextBlock));
        desc.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Description"));
        desc.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("FgSecondary"));
        desc.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        desc.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 0, 0, 0));
        desc.SetValue(FrameworkElement.MaxWidthProperty, 420.0);
        dock.AppendChild(desc);
        t.VisualTree = dock;
        return t;
    }

    private void AcceptSuggestion()
    {
        if (_suggestList.SelectedItem is not Suggestion s) return;
        var text = _input.Text;
        string result;
        if (!s.IsOption) result = s.Text + " ";
        else
        {
            var idx = text.EndsWith(" ") ? text.Length : text.LastIndexOf(' ') + 1;
            result = text[..idx] + s.Text + (s.Text.EndsWith("=") ? "" : " ");
        }
        _suggest.IsOpen = false;
        _input.Text = result;
        _input.CaretIndex = result.Length;
        _input.Focus();
    }

    private async Task LoadOptionsAsync(string command)
    {
        if (Workspace.PhpExe == null || Workspace.Root == null) return;
        OptionsCache[command] = new List<(string, string)>();
        var (code, stdout, _) = await ProcessRunner.RunAsync(Workspace.PhpExe, $"{Workspace.NeoScript} {command} --help --no-ansi", Workspace.Root, 15000);
        if (code != 0) return;
        var list = new List<(string, string)>();
        foreach (Match m in Regex.Matches(stdout, @"^\s+(?:-\w(?:\|\w+)?,\s+)?(?<opt>--[\w-]+)(?<val>=[A-Z_]+)?\s{2,}(?<desc>.+)$", RegexOptions.Multiline))
            list.Add((m.Groups["opt"].Value + (m.Groups["val"].Success ? "=" : ""), m.Groups["desc"].Value.Trim()));
        OptionsCache[command] = list;
        UpdateSuggestions();
    }

    // ------------------------------------------------------------------ colouring

    private sealed class LineColorizer : DocumentColorizingTransformer
    {
        public Dictionary<int, LineKind> Lines { get; } = new();
        public Brush ErrorBrush { get; set; } = Brushes.Red;
        public Brush WarnBrush { get; set; } = Brushes.Orange;
        public Brush InfoBrush { get; set; } = Brushes.Gray;
        public Brush OkBrush { get; set; } = Brushes.Green;
        public Brush CommandBrush { get; set; } = Brushes.Blue;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0 || !Lines.TryGetValue(line.LineNumber, out var kind)) return;
            var brush = kind switch
            {
                LineKind.Error => ErrorBrush,
                LineKind.Warning => WarnBrush,
                LineKind.Info => InfoBrush,
                LineKind.Ok => OkBrush,
                LineKind.Command => CommandBrush,
                _ => null,
            };
            if (brush == null) return;
            ChangeLinePart(line.Offset, line.EndOffset, e =>
            {
                e.TextRunProperties.SetForegroundBrush(brush);
                if (kind == LineKind.Command) e.TextRunProperties.SetTypeface(new Typeface(e.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal));
            });
        }
    }
}
