using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Search;
using NeoIDE.Php;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;
using ICSharpCode.AvalonEdit.Rendering;

namespace NeoIDE.Editor;

/// <summary>AvalonEdit based editor with PhpStorm behaviours (completion, imports, navigation, smart typing).</summary>
public sealed partial class CodeEditor : TextEditor
{
    public string? FilePath { get; private set; }
    public string? Language { get; private set; }
    public bool IsPhp => Language == "PHP";

    public event Action<string, int>? NavigateRequested;
    public event Action<string>? StatusMessage;

    private CompletionWindow? _completion;
    private FoldingManager? _foldingManager;
    private readonly BraceFoldingStrategy _braceFolding = new();
    private readonly XmlFoldingStrategy _xmlFolding = new() { ShowAttributesWhenFolded = true };
    private readonly DimColorizer _dim = new();
    private readonly SquiggleRenderer _squiggles = new();
    private readonly BracketHighlightRenderer _brackets = new();
    private readonly DispatcherTimer _analysisTimer;
    private List<(int start, int length, string name, List<PhpClass> candidates)> _unresolved = new();
    private List<PhpUse> _unusedUses = new();
    private readonly System.Windows.Controls.Primitives.Popup _hoverTip = new()
    {
        Placement = System.Windows.Controls.Primitives.PlacementMode.Relative, AllowsTransparency = true,
        PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, StaysOpen = true,
    };

    public CodeEditor()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 4;
        Options.HighlightCurrentLine = true;
        Options.EnableHyperlinks = true;
        Options.RequireControlModifierForHyperlinkClick = true;
        Options.EnableRectangularSelection = true;
        Options.AllowScrollBelowDocument = true;
        Options.CutCopyWholeLine = true;
        Options.EnableTextDragDrop = true;
        Options.ShowBoxForControlCharacters = true;
        TextArea.TextView.LineTransformers.Add(_dim);
        TextArea.TextView.BackgroundRenderers.Add(_squiggles);
        TextArea.TextView.BackgroundRenderers.Add(_brackets);
        TextArea.Margin = new Thickness(4, 0, 0, 0);
        TextArea.LeftMargins.Add(_changeMargin);
        TextArea.LeftMargins.Add(new Border { Width = 6 });
        _stripe = new ErrorStripe(TextArea.TextView, () => _squiggles.Markers, GoToOffset);
        TextArea.TextView.InsertLayer(_stripe, KnownLayer.Caret, LayerInsertionPosition.Above);
        DiagnosticsStore.Changed += OnDiagnosticsChanged;
        _breakpointMargin = new BreakpointMargin(this);
        TextArea.LeftMargins.Insert(TextArea.LeftMargins.IndexOf(_changeMargin), _neoGutter);
        TextArea.TextView.BackgroundRenderers.Add(new DebugLineRenderer(this));
        Services.Debug.Breakpoints.Changed += OnBreakpointsChanged;
        Services.Debug.DebugService.StateChanged += OnDebugStateChanged;
        Php.NeoProject.Changed += OnNeoProjectChanged;

        var search = SearchPanel.Install(this);
        search.MarkerBrush = new SolidColorBrush(EditorPalette.SearchHighlight);

        TextArea.TextEntering += OnTextEntering;
        TextArea.TextEntered += OnTextEntered;
        TextArea.Caret.PositionChanged += (_, _) => UpdateBrackets();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        MouseHover += OnMouseHover;
        MouseHoverStopped += (_, _) => { if (!_hoverTip.IsMouseOver) _hoverTip.IsOpen = false; };
        MouseLeave += (_, _) => Dispatcher.BeginInvoke(() => { if (!_hoverTip.IsMouseOver) _hoverTip.IsOpen = false; }, DispatcherPriority.Background);
        _hoverTip.MouseLeave += (_, _) => _hoverTip.IsOpen = false;
        PreviewKeyDown += (_, _) => _hoverTip.IsOpen = false;
        TextChanged += (_, _) => { _analysisTimer!.Stop(); _analysisTimer.Start(); };

        _analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _analysisTimer.Tick += (_, _) => { _analysisTimer.Stop(); Analyze(); };

        ThemeManager.ThemeChanged += ApplyTheme;
        DisplayManager.Changed += ApplyDisplay;
        InitLinks();
        TextArea.TextView.ElementGenerators.Add(_colors);
        _colors.SwatchClicked += OpenColorPicker;
        Unloaded += (_, _) => _hoverTip.IsOpen = false;
        ApplyDisplay();
        ApplyTheme();
    }

    public void Detach()
    {
        ThemeManager.ThemeChanged -= ApplyTheme;
        DisplayManager.Changed -= ApplyDisplay;
        DiagnosticsStore.Changed -= OnDiagnosticsChanged;
        Services.Debug.Breakpoints.Changed -= OnBreakpointsChanged;
        Services.Debug.DebugService.StateChanged -= OnDebugStateChanged;
        Php.NeoProject.Changed -= OnNeoProjectChanged;
    }

    // ------------------------------------------------------------------ debugger

    private readonly BreakpointMargin _breakpointMargin;

    public void ToggleBreakpoint(int line)
    {
        if (FilePath == null || !IsPhp || IsPreview) return;
        Services.Debug.Breakpoints.Toggle(FilePath, line);
    }

    private void OnBreakpointsChanged(string? file)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnBreakpointsChanged(file)); return; }
        _breakpointMargin.Sync();
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void OnDebugStateChanged()
    {
        _breakpointMargin.InvalidateVisual();
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void OnNeoProjectChanged()
    {
        // routes / templates / .env changed: recheck the NeoPHP names of the buffer
        if (!SupportsNeo) return;
        Dispatcher.BeginInvoke(() => { _analysisTimer.Stop(); _analysisTimer.Start(); });
    }

    /// <summary>Breakpoints first, then the line numbers (AvalonEdit inserts them at index 0 when they are switched on).</summary>
    private void ArrangeMargins()
    {
        var margins = TextArea.LeftMargins;
        if (margins.IndexOf(_breakpointMargin) == 0) return;
        margins.Remove(_breakpointMargin);
        margins.Insert(0, _breakpointMargin);
    }

    // ------------------------------------------------------------------ problems (own analysis + php -l, PHPStan, NeoPHP checks)

    private readonly ErrorStripe _stripe;
    private List<Squiggle> _ideMarkers = new();
    private int _lintVersion;

    private void OnDiagnosticsChanged(string? file)
    {
        if (file != null && (FilePath == null || !string.Equals(file, System.IO.Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnDiagnosticsChanged(file)); return; }
        ComposeMarkers();
    }

    /// <summary>Problems published by the other sources, converted to ranges of the buffer.</summary>
    private List<Squiggle> ExternalMarkers()
    {
        var list = new List<Squiggle>();
        if (FilePath == null || IsPreview) return list;
        foreach (var d in DiagnosticsStore.For(FilePath))
        {
            if (d.Source is "ide" or "neophp") continue; // computed on the buffer: already in _ideMarkers
            int start, length;
            if (d.Start >= 0 && d.Start <= Document.TextLength) { start = d.Start; length = Math.Min(d.Length, Document.TextLength - d.Start); }
            else
            {
                var line = Document.GetLineByNumber(Math.Clamp(d.Line, 1, Document.LineCount));
                var text = Document.GetText(line);
                if (d.Column > 0 && d.Column <= text.Length)
                {
                    start = line.Offset + d.Column - 1;
                    int e = d.Column - 1;
                    while (e < text.Length && (PhpLexer.IsIdentPart(text[e]) || text[e] is '$' or '\\')) e++;
                    length = Math.Max(1, e - (d.Column - 1));
                }
                else
                {
                    var ws = text.Length - text.TrimStart().Length;
                    start = line.Offset + ws;
                    length = Math.Max(1, text.TrimEnd().Length - ws);
                }
            }
            var source = d.Source switch { "php" => "PHP", "phpstan" => "PHPStan", "neophp" => "NeoPHP", var s => s };
            list.Add(new Squiggle(start, length, $"{d.Message}{(d.Code != null ? $"  ({d.Code})" : "")}\n— {source}", d.Severity, d.Source));
        }
        return list;
    }

    /// <summary>The IDE's own problems of the buffer, for the Problems tool window.</summary>
    private void PublishIdeMarkers(string path)
    {
        var doc = Document;
        DiagnosticsStore.Set(path, "ide", _ideMarkers.Where(m => m.Start >= 0 && m.Start <= doc.TextLength).Select(m =>
        {
            var line = doc.GetLineByOffset(m.Start);
            return new Diagnostic(path, line.LineNumber, m.Start - line.Offset + 1, m.Severity, m.Message.Split('\n')[0], m.Source)
                { Start = m.Start, Length = m.Length };
        }).ToList());
    }

    private void ComposeMarkers()
    {
        _squiggles.Markers = _ideMarkers.Concat(ExternalMarkers()).ToList();
        _stripe.Refresh();
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        ProblemsChanged?.Invoke();
    }

    public event Action? ProblemsChanged;

    /// <summary>Problems of the buffer, in document order.</summary>
    public IReadOnlyList<Squiggle> Problems => _squiggles.Markers.OrderBy(m => m.Start).ToList();

    /// <summary>F2 / Shift+F2: next / previous problem of the file (errors first, like PhpStorm).</summary>
    public void GoToProblem(int dir)
    {
        var all = Problems.Where(m => m.Severity != Severity.Info).ToList();
        if (all.Count == 0) { StatusMessage?.Invoke("No problems in this file"); return; }
        var pool = all.Any(m => m.Severity == Severity.Error) ? all.Where(m => m.Severity == Severity.Error).ToList() : all;
        var caret = CaretOffset;
        var target = dir > 0 ? pool.FirstOrDefault(m => m.Start > caret) ?? pool[0] : pool.LastOrDefault(m => m.Start < caret) ?? pool[^1];
        GoToOffset(target.Start);
        StatusMessage?.Invoke(target.Message.Split('\n')[0]);
    }

    private async void LintBuffer(string path, string text)
    {
        var version = ++_lintVersion;
        await Task.Delay(400);
        if (version != _lintVersion) return;
        try
        {
            var list = await Checkers.LintAsync(path, text);
            if (version != _lintVersion) return;
            DiagnosticsStore.Set(path, "php", list);
        }
        catch { }
    }

    /// <summary>Editor part of the Appearance &amp; display settings.</summary>
    public void ApplyDisplay()
    {
        var s = AppSettings.Current;
        FontFamily = DisplayManager.EditorFont;
        FontSize = s.EditorFontSize;
        WordWrap = s.EditorWordWrap;
        HorizontalScrollBarVisibility = s.EditorWordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ShowLineNumbers = s.EditorShowLineNumbers;
        Options.ShowSpaces = s.EditorShowWhitespace;
        Options.ShowTabs = s.EditorShowWhitespace;
        Options.HighlightCurrentLine = s.EditorHighlightCurrentLine;
        foreach (var margin in TextArea.LeftMargins.OfType<FoldingMargin>())
            margin.Visibility = s.EditorShowFolding ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_breakpointMargin != null) ArrangeMargins();
    }

    public void Load(string path, string text)
    {
        FilePath = path;
        Language = SyntaxRegistry.DetectLanguage(path);
        // A PHP file without "<?php" is a template: still PHP grammar.
        SyntaxHighlighting = SyntaxRegistry.Get(Language);
        Document = new TextDocument(text);
        Document.UndoStack.ClearAll();
        TextArea.IndentationStrategy = new SmartIndentation(Language == "YAML");
        _colors.Language = Language;
        _colors.Enabled = Language is "CSS" or "HTML" or "Twig" or "PHP" or "JavaScript" or "JSON" or "YAML" or "XML" or "Markdown";
        if (Language is "JSON" or "JavaScript" or "CSS" && Regex.IsMatch(text, @"^\s{2}\S", RegexOptions.Multiline) && !Regex.IsMatch(text, @"^\s{4}\S", RegexOptions.Multiline))
            Options.IndentationSize = 2;
        _foldingManager = FoldingManager.Install(TextArea);
        StyleFoldingMargin();
        ApplyDisplay();
        _breakpointMargin.Sync();
        UpdateFoldings();
        Analyze();
    }

    // ------------------------------------------------------------------ theme

    public void ApplyTheme()
    {
        Background = new SolidColorBrush(EditorPalette.Background);
        Foreground = new SolidColorBrush(EditorPalette.Foreground);
        LineNumbersForeground = new SolidColorBrush(EditorPalette.LineNumbers);
        TextArea.SelectionBrush = new SolidColorBrush(EditorPalette.Selection);
        TextArea.SelectionForeground = null;
        TextArea.SelectionBorder = null;
        TextArea.SelectionCornerRadius = 0;
        TextArea.TextView.CurrentLineBackground = new SolidColorBrush(EditorPalette.CurrentLine);
        TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        TextArea.TextView.LinkTextForegroundBrush = ThemeManager.Brush("LinkFg");
        TextArea.Caret.CaretBrush = new SolidColorBrush(EditorPalette.Caret);
        StyleFoldingMargin();
        var h = SyntaxHighlighting;
        SyntaxHighlighting = null;
        SyntaxHighlighting = h;
        TextArea.TextView.Redraw();
    }

    /// <summary>Flat, theme-coloured folding markers (the AvalonEdit defaults are black/white boxes).</summary>
    private void StyleFoldingMargin()
    {
        foreach (var margin in TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.FoldingMarkerBrush = new SolidColorBrush(EditorPalette.LineNumbers);
            margin.FoldingMarkerBackgroundBrush = new SolidColorBrush(EditorPalette.Background);
            margin.SelectedFoldingMarkerBrush = new SolidColorBrush(EditorPalette.ActiveLineNumber);
            margin.SelectedFoldingMarkerBackgroundBrush = new SolidColorBrush(EditorPalette.CurrentLine);
            margin.Margin = new Thickness(2, 0, 2, 0);
        }
    }

    // ------------------------------------------------------------------ analysis

    private void UpdateFoldings()
    {
        if (_foldingManager == null) return;
        try
        {
            if (Language is "HTML" or "XML" or "Twig")
                _xmlFolding.UpdateFoldings(_foldingManager, Document);
            else if (Language is "PHP" or "JavaScript" or "CSS" or "JSON")
                _foldingManager.UpdateFoldings(_braceFolding.CreateFoldings(Document), -1);
        }
        catch { }
    }

    /// <summary>Read-only previews (diff, settings) must not feed the PHP index nor show squiggles.</summary>
    public bool IsPreview { get; set; }

    /// <summary>HEAD version of the file (null = not tracked / no repository) used for the gutter change markers.</summary>
    public string? GitBaseText
    {
        get => _changeMargin.BaseText;
        set { _changeMargin.BaseText = value; Analyze(); }
    }

    private readonly ChangeMarkerMargin _changeMargin = new();
    private int _analysisVersion;
    private List<MissingMethods> _missing = new();

    private sealed record AnalysisResult(object? Marks, List<PhpUse>? Unused, List<(int, int)>? Dim,
        List<(int start, int length, string name, List<PhpClass> candidates)>? Unresolved, List<MissingMethods>? Missing,
        List<Squiggle>? Neo = null);

    /// <summary>
    /// Background analysis (parse, imports, unknown classes, missing methods, VCS markers):
    /// only the results are applied on the UI thread, so typing never waits for it.
    /// </summary>
    private async void Analyze()
    {
        UpdateFoldings();
        var version = ++_analysisVersion;
        var text = Document.Text;
        var baseText = IsPreview ? null : _changeMargin.BaseText;
        var php = IsPhp && !IsPreview;
        var path = FilePath;
        var language = Language;
        var neoChecks = !IsPreview && path != null && Language is "PHP" or "Twig" or "YAML";
        AnalysisResult result;
        try
        {
            result = await Task.Run(() =>
            {
                var marks = baseText != null ? ChangeMarkerMargin.Prepare(baseText, text) : null;
                var neo = neoChecks ? NeoInsight.Check(text, language) : null;
                if (neoChecks && language == "Twig") neo!.AddRange(TwigInsight.Check(text, path));
                if (neoChecks && language == "PHP") neo!.AddRange(TemplateVariables.CheckPhpView(text, path!));
                if (!php) return new AnalysisResult(marks, null, null, null, null, neo);
                var info = PhpParser.Parse(text, path);
                if (path != null && Workspace.Root != null) Workspace.Index.UpdateFromBuffer(info, path);
                var unused = ImportManager.FindUnused(text, info);
                var dim = new List<(int, int)>();
                foreach (var group in info.Uses.GroupBy(u => u.StatementStart))
                {
                    var items = group.ToList();
                    if (items.All(unused.Contains)) dim.Add((items[0].StatementStart, items[0].StatementEnd));
                }
                var insight = Workspace.Insight;
                return new AnalysisResult(marks, unused, dim, insight.FindUnresolvedClasses(text, info), insight.FindMissingImplementations(info), neo);
            });
        }
        catch { return; }
        if (version != _analysisVersion) return; // the text changed meanwhile: a newer analysis is coming

        _changeMargin.Apply(result.Marks);
        _neoGutter.Update(this);
        if (!php)
        {
            if (!neoChecks) return;
            _ideMarkers = result.Neo ?? new List<Squiggle>();
            PublishIdeMarkers(path!);
            ComposeMarkers();
            return;
        }
        _unusedUses = result.Unused!;
        _dim.Segments = result.Dim!;
        _unresolved = result.Unresolved!;
        _missing = result.Missing!;
        var markers = _unresolved.Select(u => new Squiggle(u.start, u.length, u.candidates.Count > 0
            ? $"Undefined class '{u.name}' — Alt+Enter to import" : $"Undefined class '{u.name}'", Severity.Error)).ToList();
        foreach (var m in _missing)
            markers.Add(new Squiggle(m.NameOffset, m.NameLength,
                $"Class '{m.ClassName}' must implement {m.Methods.Count} method(s): {string.Join(", ", m.Methods.Take(6).Select(x => $"{x.Name}() of {x.DeclaringClass.Split('\\').Last()}"))}{(m.Methods.Count > 6 ? "…" : "")} — Alt+Enter / Ctrl+I to add them", Severity.Error));
        markers.AddRange(result.Neo ?? new List<Squiggle>());
        _ideMarkers = markers;
        if (path != null) PublishIdeMarkers(path);
        ComposeMarkers();
        if (path != null && AppSettings.Current.LintWhileTyping && Workspace.PhpExe != null) LintBuffer(path, text);
    }

    private void UpdateBrackets()
    {
        var (a, b) = BracketHighlightRenderer.FindPair(Document, CaretOffset);
        if (a != _brackets.First || b != _brackets.Second)
        {
            _brackets.First = a;
            _brackets.Second = b;
            TextArea.TextView.InvalidateLayer(_brackets.Layer);
        }
        var line = Document.GetLineByOffset(CaretOffset);
        StatusMessage?.Invoke($"{line.LineNumber}:{CaretOffset - line.Offset + 1}");
    }

    // ------------------------------------------------------------------ typing

    private static readonly Dictionary<char, char> Pairs = new() { ['('] = ')', ['['] = ']', ['{'] = '}', ['"'] = '"', ['\''] = '\'', ['`'] = '`' };

    private char CharAt(int offset) => offset >= 0 && offset < Document.TextLength ? Document.GetCharAt(offset) : '\0';

    private void OnTextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0) return;
        char c = e.Text[0];

        if (_completion != null)
        {
            if (c == '(' && _completion.CompletionList.SelectedItem != null)
            {
                _completion.CompletionList.RequestInsertion(e);
                // the item already inserted "()": swallow the typed "("
                if (CharAt(CaretOffset - 1) == '(' || CharAt(CaretOffset - 1) == ')') e.Handled = true;
                return;
            }
            if (!PhpLexer.IsIdentPart(c) && c != '\\' && c != '$')
            {
                _completion.Close();
            }
        }

        // type over closing chars
        if (c is ')' or ']' or '}' or '"' or '\'' or '`' && CharAt(CaretOffset) == c && TextArea.Selection.IsEmpty)
        {
            CaretOffset++;
            e.Handled = true;
            return;
        }

        // surround selection
        if (!TextArea.Selection.IsEmpty && Pairs.TryGetValue(c, out var closeSel) && c != '{')
        {
            var sel = TextArea.Selection.GetText();
            var start = SelectionStart;
            Document.Replace(start, SelectionLength, c + sel + closeSel);
            Select(start + 1, sel.Length);
            e.Handled = true;
        }
    }

    private void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0) return;
        char c = e.Text[0];
        int caret = CaretOffset;

        // auto-close pairs
        if (Pairs.TryGetValue(c, out var close))
        {
            var next = CharAt(caret);
            var prev = CharAt(caret - 2);
            bool quote = c is '"' or '\'' or '`';
            bool okNext = next == '\0' || char.IsWhiteSpace(next) || ")]};,".Contains(next);
            bool okPrev = !quote || !(PhpLexer.IsIdentPart(prev) || prev == '\\');
            if (okNext && okPrev && !(c == '{' && Language is "Markdown") && !IsInComment(caret - 1))
            {
                Document.Insert(caret, close.ToString());
                CaretOffset = caret;
            }
        }
        if (c == '}') Reindent(Document.GetLineByOffset(caret));

        // NeoPHP names: route, template, .env variable, parameter, service
        if (_completion == null && SupportsNeo && (c is '\'' or '"' or '(' or '%' or '@' || PhpLexer.IsIdentPart(c)) && NeoRefAt(CaretOffset) != null)
        {
            ShowCompletion(false);
            return;
        }

        // CSS (files, <style> blocks, style="" attributes): first letter of a word, ':', '@', '!', '.', '--'
        if (_completion == null && (Language == "CSS" || Language is "HTML" or "Twig" or "PHP" && CssRegion(Text, caret) != null))
        {
            var p = CharAt(caret - 2);
            bool wordStart = (char.IsLetter(c) || c == '-') && !(char.IsLetterOrDigit(p) || p is '-' or '_' or '#' or '$');
            if (wordStart || c is ':' or '@' or '!' || c == '.' && !char.IsDigit(p) || c == '-' && p == '-' || c == '#' && Language == "CSS")
                ShowCompletion(false);
            return;
        }

        if (!IsPhp)
        {
            // Twig: variables, attributes after ".", filters after "|", tags after "{%"
            if (Language is "Twig" && _completion == null && !IsPreview)
            {
                var p = CharAt(caret - 2);
                bool wordStart = (char.IsLetter(c) || c == '_') && !(char.IsLetterOrDigit(p) || p is '_' or '.');
                if ((wordStart || c is '.' or '|') && TwigInsight.InExpression(Text, caret)) ShowCompletion(false);
            }
            return;
        }

        // PHP completion triggers
        if (c == '>' && CharAt(caret - 2) == '-') { ShowCompletion(false); return; }
        if (c == ':' && CharAt(caret - 2) == ':') { ShowCompletion(false); return; }
        if (c == '$' || c == '\\' || c == '@') { ShowCompletion(false); return; }
        if ((c == '\'' || c == '"') && CharAt(caret - 2) == '(') { ShowCompletion(false); return; }
        if (c == '[' && CharAt(caret - 2) == '#') { ShowCompletion(true); return; }
        if (_completion == null && PhpLexer.IsIdentStart(c))
        {
            // first letter of a word
            var p = CharAt(caret - 2);
            if (!PhpLexer.IsIdentPart(p) && p != '$') ShowCompletion(false);
        }
    }

    private bool IsInComment(int offset)
    {
        var line = Document.GetLineByOffset(Math.Max(0, offset));
        var before = Document.GetText(line.Offset, Math.Max(0, offset - line.Offset));
        return before.Contains("//") || before.TrimStart().StartsWith("*") || before.TrimStart().StartsWith("#") && Language is "YAML" or "Shell" or "ENV";
    }

    private void Reindent(DocumentLine line)
    {
        try { TextArea.IndentationStrategy?.IndentLine(Document, line); } catch { }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // AltGr is reported as Ctrl+Alt: it types characters (@ # { } [ ] \ | ~ on AZERTY), never a shortcut
        if (KeyboardHelper.IsAltGr) return;

        if (key == Key.Space && mods == ModifierKeys.Control) { ShowCompletion(true); e.Handled = true; return; }
        if (key == Key.Enter && mods == ModifierKeys.Alt) { ShowQuickFixes(); e.Handled = true; return; }
        if (key == Key.B && mods == ModifierKeys.Control) { GoToDeclaration(CaretOffset); e.Handled = true; return; }
        if (key == Key.I && mods == ModifierKeys.Control && IsPhp) { ImplementMethodsAtCaret(); e.Handled = true; return; }
        if (key == Key.D && mods == ModifierKeys.Control) { DuplicateLine(); e.Handled = true; return; }
        if (key == Key.Y && mods == ModifierKeys.Control) { DeleteLine(); e.Handled = true; return; }
        if (key == Key.Oem2 && mods == ModifierKeys.Control || key == Key.Divide && mods == ModifierKeys.Control) { ToggleComment(); e.Handled = true; return; }
        if (key == Key.O && mods == (ModifierKeys.Control | ModifierKeys.Alt)) { OptimizeImports(); e.Handled = true; return; }
        if (key == Key.L && mods == (ModifierKeys.Control | ModifierKeys.Alt)) { _ = ReformatAsync(); e.Handled = true; return; }
        if (key is Key.Up or Key.Down && mods == (ModifierKeys.Control | ModifierKeys.Shift)) { MoveLine(key == Key.Up ? -1 : 1); e.Handled = true; return; }
        if (key == Key.Escape && _completion == null) { _hoverTip.IsOpen = false; }
        if (key == Key.F2 && mods is ModifierKeys.None or ModifierKeys.Shift) { GoToProblem(mods == ModifierKeys.Shift ? -1 : 1); e.Handled = true; return; }
        if (key == Key.F8 && mods == ModifierKeys.Control) { ToggleBreakpoint(Document.GetLineByOffset(CaretOffset).LineNumber); e.Handled = true; return; }

        if (key == Key.Back && mods == ModifierKeys.None && TextArea.Selection.IsEmpty)
        {
            var prev = CharAt(CaretOffset - 1);
            var next = CharAt(CaretOffset);
            if (Pairs.TryGetValue(prev, out var cl) && cl == next)
            {
                Document.Remove(CaretOffset - 1, 2);
                e.Handled = true;
            }
            return;
        }

        if (key == Key.Enter && mods == ModifierKeys.None && _completion == null)
        {
            if (TryExpandDocBlock()) { e.Handled = true; return; }
            var prev = CharAt(CaretOffset - 1);
            var next = CharAt(CaretOffset);
            if ((prev == '{' && next == '}') || (prev == '[' && next == ']') || (prev == '(' && next == ')'))
            {
                var line = Document.GetLineByOffset(CaretOffset);
                var indent = LeadingWhitespace(Document.GetText(line));
                var nl = Document.Text.Contains("\r\n") ? "\r\n" : "\n";
                var inner = indent + new string(' ', Options.IndentationSize);
                var caret = CaretOffset;
                Document.Insert(caret, nl + inner + nl + indent);
                CaretOffset = caret + nl.Length + inner.Length;
                e.Handled = true;
                return;
            }
            // continue "*" lines in doc comments
            var curLine = Document.GetLineByOffset(CaretOffset);
            var lineText = Document.GetText(curLine.Offset, CaretOffset - curLine.Offset);
            if (Regex.IsMatch(lineText, @"^\s*\*(?!/)") || Regex.IsMatch(lineText, @"^\s*/\*\*"))
            {
                var ws = LeadingWhitespace(lineText);
                var prefix = lineText.TrimStart().StartsWith("/**") ? ws + " * " : ws + "* ";
                var nl = Document.Text.Contains("\r\n") ? "\r\n" : "\n";
                Document.Insert(CaretOffset, nl + prefix);
                e.Handled = true;
            }
        }
    }

    private static string LeadingWhitespace(string s) => new(s.TakeWhile(ch => ch is ' ' or '\t').ToArray());

    private bool TryExpandDocBlock()
    {
        var line = Document.GetLineByOffset(CaretOffset);
        var before = Document.GetText(line.Offset, CaretOffset - line.Offset);
        var after = Document.GetText(CaretOffset, line.EndOffset - CaretOffset);
        if (!before.TrimEnd().EndsWith("/**") || after.Trim().Length > 0 && after.Trim() != "*/") return false;
        var ws = LeadingWhitespace(before);
        var nl = Document.Text.Contains("\r\n") ? "\r\n" : "\n";

        // look at the next declaration to generate @param / @return
        var tags = new List<string>();
        if (IsPhp && line.NextLine != null)
        {
            var nextText = Document.GetText(line.NextLine.Offset, Math.Min(600, Document.TextLength - line.NextLine.Offset));
            var m = Regex.Match(nextText, @"^\s*(?:(?:public|protected|private|static|abstract|final)\s+)*function\s+&?\w+\s*\((?<params>[^)]*)\)\s*(?::\s*(?<ret>[\w\\|?]+))?");
            if (m.Success)
            {
                foreach (var p in m.Groups["params"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pm = Regex.Match(p.Trim(), @"(?:(?:public|protected|private|readonly)\s+)*(?<type>[\w\\|?]+\s+)?&?(?:\.\.\.)?(?<name>\$\w+)");
                    if (pm.Success) tags.Add($"@param {(pm.Groups["type"].Value.Trim() is { Length: > 0 } t ? t : "mixed")} {pm.Groups["name"].Value}");
                }
                var ret = m.Groups["ret"].Value;
                if (ret.Length > 0 && ret != "void") tags.Add("@return " + ret);
            }
        }
        var sb = new System.Text.StringBuilder();
        sb.Append(nl).Append(ws).Append(" * ");
        int caretPos = sb.Length;
        if (tags.Count > 0)
        {
            sb.Append(nl).Append(ws).Append(" *");
            foreach (var t in tags) sb.Append(nl).Append(ws).Append(" * ").Append(t);
        }
        sb.Append(nl).Append(ws).Append(" */");
        var insertAt = CaretOffset;
        // remove an auto inserted "*/"
        if (after.Trim() == "*/") Document.Remove(insertAt, after.Length);
        Document.Insert(insertAt, sb.ToString());
        CaretOffset = insertAt + caretPos;
        return true;
    }

    // ------------------------------------------------------------------ completion

    public void ShowCompletion(bool explicitInvoke)
    {
        if (_completion != null && !explicitInvoke) return;
        _completion?.Close();
        var caret = CaretOffset;
        var text = Text;
        List<CompletionEntry> entries;
        int start;

        var css = CssRegion(text, caret);
        var neo = NeoCompletionAt(caret);
        _neoCompletion = neo != null;
        if (neo != null)
        {
            start = neo.Value.start;
            entries = neo.Value.entries;
        }
        else if (css != null)
        {
            var r = CssCompletion.Complete(css.Value.css, caret - css.Value.start, css.Value.start, css.Value.declarations, Workspace.Root);
            start = r.ReplaceStart;
            // "prop" followed by an existing ":" → insert the name only
            var after = text[caret..Math.Min(text.Length, caret + 40)].TrimStart(' ', '\t');
            entries = r.Entries.Select(e => e.Kind == EntryKind.Property && after.StartsWith(":")
                ? new CompletionEntry { Label = e.Label, Insert = e.Label, Kind = e.Kind, Priority = e.Priority } : e).ToList();
        }
        else if (Language == "Twig" && !IsPreview && TwigInsight.Complete(text, caret, FilePath) is { } twig)
        {
            start = twig.start;
            entries = twig.entries;
        }
        else if (IsPhp)
        {
            CompletionResult result;
            // PHP views: the variables given by render() (and $this, the Template) are declared before the buffer
            var prelude = !IsPreview && TemplateVariables.IsPhpView(FilePath) ? TemplateVariables.PhpViewPrelude(FilePath!) : "";
            try { result = Workspace.Completion.Complete(prelude + text, caret + prelude.Length, FilePath, explicitInvoke); }
            catch (Exception ex) { StatusMessage?.Invoke("Completion error: " + ex.Message); return; }
            entries = result.Entries;
            start = Math.Max(0, result.ReplaceStart - prelude.Length);
            if (prelude.Length > 0 && start < text.Length && text[start] == '$')
                foreach (var v in TemplateVariables.For(FilePath))
                    if (entries.All(x => x.Label != "$" + v.Name))
                        entries.Add(new CompletionEntry { Label = "$" + v.Name, Insert = "$" + v.Name, Kind = EntryKind.Variable, TypeText = PhpTypes.Short(v.Type), Tail = $"  {v.Source}", Priority = 10 });
        }
        else
        {
            // word based completion for the other languages
            start = caret;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-' or '$')) start--;
            var prefix = text[start..caret];
            if (prefix.Length == 0 && !explicitInvoke) return;
            entries = Regex.Matches(text, @"[\w$][\w-]{2,}").Select(m => m.Value).Distinct()
                .Where(w => w != prefix && w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Take(400).Select(w => new CompletionEntry { Label = w, Insert = w, Kind = EntryKind.Keyword }).ToList();
        }
        var typed = text[start..caret];
        _allEntries = entries;
        var visible = Filter(entries, typed);
        if (visible.Count == 0) return;

        _completion = new CompletionWindow(TextArea)
        {
            StartOffset = start,
            EndOffset = caret,
            CloseWhenCaretAtBeginning = !explicitInvoke && typed.Length > 0,
            CloseAutomatically = true,
            Width = 560,
            MaxHeight = 420,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = ThemeManager.Brush("PopupBg"),
            BorderBrush = ThemeManager.Brush("PopupBorder"),
            BorderThickness = new Thickness(1),
            Foreground = ThemeManager.Brush("Fg"),
        };
        StyleCompletionList(_completion.CompletionList);
        // our own strict filtering (prefix / camel humps) instead of AvalonEdit's "contains" filtering
        _completion.CompletionList.IsFiltering = false;
        Fill(visible);
        var window = _completion;
        Document.TextChanged += Refilter;
        window.Closed += (_, _) => { Document.TextChanged -= Refilter; if (_completion == window) _completion = null; };
        window.Show();
    }

    private List<CompletionEntry> _allEntries = new();
    private const int MaxVisibleEntries = 300;

    private static List<CompletionEntry> Filter(List<CompletionEntry> entries, string typed)
    {
        var t = typed.TrimStart('$');
        var isVar = typed.StartsWith("$");
        return entries
            .Where(e => PhpCompletion.Matches(isVar ? e.Label.TrimStart('$') : e.Label, t) ||
                        e.Label.Contains('\\') && PhpCompletion.Matches(e.Label, typed))
            .OrderByDescending(e => e.Label.TrimStart('$').StartsWith(t, StringComparison.Ordinal))      // exact case prefix first
            .ThenByDescending(e => e.Label.TrimStart('$').StartsWith(t, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Priority)
            .ThenBy(e => e.Label.Length)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxVisibleEntries)
            .ToList();
    }

    private void Fill(List<CompletionEntry> visible)
    {
        if (_completion == null) return;
        var data = _completion.CompletionList.CompletionData;
        data.Clear();
        foreach (var en in visible) data.Add(new CompletionData(en) { AfterInsert = OnCompletionInserted });
        _completion.CompletionList.SelectedItem = data.FirstOrDefault();
    }

    /// <summary>Typing (or deleting) while the list is open: re-filter, close when nothing matches.</summary>
    private void Refilter(object? sender, EventArgs e)
    {
        var w = _completion;
        if (w == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_completion != w) return;
            var caret = CaretOffset;
            if (caret < w.StartOffset) { w.Close(); return; }
            var typed = Document.GetText(w.StartOffset, caret - w.StartOffset);
            if (typed.Any(ch => !(PhpLexer.IsIdentPart(ch) || ch is '$' or '\\' || _neoCompletion && ch is '/' or '.' or '-' or '@' or ':'))) { w.Close(); return; }
            var visible = Filter(_allEntries, typed);
            // nothing left, or the only proposal is exactly what was typed: close
            if (visible.Count == 0 || visible.Count == 1 && visible[0].Label == typed) { w.Close(); return; }
            Fill(visible);
        }, DispatcherPriority.Background);
    }

    private static void StyleCompletionList(CompletionList list)
    {
        list.BorderThickness = new Thickness(0);
        list.Background = ThemeManager.Brush("PopupBg");
        list.Loaded += (_, _) =>
        {
            var lb = list.ListBox;
            if (lb == null) return;
            lb.Background = ThemeManager.Brush("PopupBg");
            lb.Foreground = ThemeManager.Brush("Fg");
            lb.BorderThickness = new Thickness(0);
            lb.Padding = new Thickness(2);
            lb.Resources[SystemColors.HighlightBrushKey] = ThemeManager.Brush("SelectedBg");
            lb.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = ThemeManager.Brush("SelectedBg");
            lb.Resources[SystemColors.HighlightTextBrushKey] = ThemeManager.Brush("Fg");
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, ThemeManager.Brush("Fg")));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var template = new ControlTemplate(typeof(ListBoxItem));
            var border = new FrameworkElementFactory(typeof(Border), "b");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.AppendChild(cp);
            template.VisualTree = border;
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Border.BackgroundProperty, ThemeManager.Brush("SelectedBg"), "b"));
            template.Triggers.Add(sel);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            lb.ItemContainerStyle = style;
            var itemTemplate = new DataTemplate();
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content"));
            itemTemplate.VisualTree = content;
            lb.ItemTemplate = itemTemplate;
        };
    }

    /// <summary>CSS file, or a &lt;style&gt; block / style="..." attribute of a template.</summary>
    private (string css, int start, bool declarations)? CssRegion(string text, int caret)
    {
        if (Language == "CSS") return (text, 0, false);
        if (Language is "HTML" or "Twig" or "PHP") return CssCompletion.EmbeddedCss(text, caret);
        return null;
    }

    private void OnCompletionInserted(CompletionEntry entry, TextArea area)
    {
        // CSS: after "prop: ;" propose the values, after "var()" the variables
        if (CssRegion(Text, CaretOffset) != null &&
            (entry.Kind == EntryKind.Property && entry.Insert.EndsWith(": ;") || entry.Insert == "var()"))
            Dispatcher.BeginInvoke(() => ShowCompletion(true), DispatcherPriority.Background);
        // "new Foo()" / "#[Column()]": caret inside the parentheses when the constructor takes arguments
        if (entry.CtorFqn != null && CharAt(CaretOffset - 1) == ')' &&
            Workspace.Index.FindMember(entry.CtorFqn, "__construct", PhpMemberKind.Method)?.Member.Parameters.Count > 0)
            CaretOffset--;
        if (entry.ImportFqn != null && IsPhp)
        {
            var text = Text;
            var info = PhpParser.Parse(text, FilePath);
            var (_, edit) = ImportManager.PrepareClassReference(text, info, entry.ImportFqn);
            if (edit is { } ed)
            {
                Document.Insert(ed.Offset, ed.Text);
                StatusMessage?.Invoke($"Imported {entry.ImportFqn}");
            }
        }
        // chain completion after "::" for class names is common: nothing else to do
        if (entry.Kind == EntryKind.Namespace) Dispatcher.BeginInvoke(() => ShowCompletion(false), DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ quick fixes / imports

    public void ShowQuickFixes()
    {
        var caret = CaretOffset;
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint };
        var pos = TextArea.TextView.GetVisualPosition(new TextViewPosition(Document.GetLocation(caret)), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom)
                  - TextArea.TextView.ScrollOffset;
        menu.HorizontalOffset = pos.X + 40;
        menu.VerticalOffset = pos.Y;

        var u = _unresolved.FirstOrDefault(x => caret >= x.start && caret <= x.start + x.length);
        if (u.name != null)
        {
            foreach (var cand in u.candidates.OrderBy(c => c.File?.Contains("\\vendor\\") == true ? 1 : 0).ThenBy(c => c.Fqn))
            {
                var fqn = cand.Fqn;
                var item = new MenuItem { Header = $"Import class '{fqn}'" };
                item.Click += (_, _) => ImportClass(fqn);
                menu.Items.Add(item);
            }
            if (u.candidates.Count == 0)
                menu.Items.Add(new MenuItem { Header = $"Class '{u.name}' not found in the project", IsEnabled = false });
        }
        var miss = _missing.FirstOrDefault(m => caret >= m.NameOffset && caret <= m.NameOffset + m.NameLength)
                   ?? (_missing.Count == 1 && menu.Items.Count == 0 ? _missing[0] : null);
        if (miss != null)
        {
            var names = string.Join(", ", miss.Methods.Take(4).Select(m => $"{m.Name}() from {m.DeclaringClass.Split('\\').Last()}")) + (miss.Methods.Count > 4 ? "…" : "");
            var impl = new MenuItem { Header = $"Implement methods: {names}", InputGestureText = "Ctrl+I" };
            impl.Click += (_, _) => ImplementMethods(miss);
            menu.Items.Insert(0, impl);
        }
        if (_unusedUses.Count > 0)
        {
            var opt = new MenuItem { Header = "Optimize imports", InputGestureText = "Ctrl+Alt+O" };
            opt.Click += (_, _) => OptimizeImports();
            menu.Items.Add(opt);
        }
        AddTemplateQuickFixes(menu, caret);
        if (menu.Items.Count == 0) { StatusMessage?.Invoke("No quick fix available here"); return; }
        menu.IsOpen = true;
    }

    /// <summary>Ctrl+I: implements the missing methods of the class around the caret.</summary>
    public void ImplementMethodsAtCaret()
    {
        var info = PhpParser.Parse(Text, FilePath);
        Workspace.Index.UpdateFromBuffer(info, FilePath);
        var cls = info.ClassAt(CaretOffset) ?? info.Classes.FirstOrDefault();
        var miss = cls == null ? null : Workspace.Insight.FindMissingImplementations(info).FirstOrDefault(m => m.ClassFqn == cls.Fqn);
        if (miss == null) { StatusMessage?.Invoke("No methods to implement"); return; }
        ImplementMethods(miss);
    }

    /// <summary>Generates the stubs of the missing methods before the closing brace of the class and imports their types.</summary>
    public void ImplementMethods(MissingMethods miss)
    {
        // fresh positions (the analysis may be a few keystrokes old)
        var text = Text;
        var info = PhpParser.Parse(text, FilePath);
        var cls = info.Classes.FirstOrDefault(c => c.Fqn == miss.ClassFqn);
        if (cls == null) return;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var toImport = new List<string>();
        string ClassRef(string fqn)
        {
            var (name, edit) = ImportManager.PrepareClassReference(text, info, fqn);
            if (edit != null && !toImport.Contains(fqn)) toImport.Add(fqn);
            return name;
        }
        var stubs = PhpInsight.BuildStubs(miss.Methods, ClassRef, new string(' ', Options.IndentationSize), nl);
        // insert before the closing brace of the class
        var close = cls.BodyEnd;
        if (close <= 0 || close >= Document.TextLength || Document.GetCharAt(close) != '}') { StatusMessage?.Invoke("Close the class body first"); return; }
        var lineStart = Document.GetLineByOffset(close).Offset;
        var beforeBrace = Document.GetText(lineStart, close - lineStart);
        var insertAt = beforeBrace.Trim().Length == 0 ? lineStart : close;
        var body = Document.GetText(cls.BodyStart + 1, Math.Max(0, close - cls.BodyStart - 1));
        if (body.Trim().Length == 0) stubs = stubs.TrimStart('\r', '\n');
        Document.BeginUpdate();
        Document.Insert(insertAt, insertAt == lineStart ? stubs : nl + stubs);
        foreach (var fqn in toImport)
        {
            var t = Text;
            var (_, edit) = ImportManager.PrepareClassReference(t, PhpParser.Parse(t, FilePath), fqn);
            if (edit is { } ed) Document.Insert(ed.Offset, ed.Text);
        }
        Document.EndUpdate();
        StatusMessage?.Invoke($"Implemented {miss.Methods.Count} method(s)");
        Analyze();
    }

    public void ImportClass(string fqn)
    {
        var text = Text;
        var info = PhpParser.Parse(text, FilePath);
        var (_, edit) = ImportManager.PrepareClassReference(text, info, fqn);
        if (edit is { } e)
        {
            Document.Insert(e.Offset, e.Text);
            StatusMessage?.Invoke($"Imported {fqn}");
        }
        Analyze();
    }

    public void OptimizeImports()
    {
        if (!IsPhp) return;
        var text = Text;
        var edit = ImportManager.Optimize(text, PhpParser.Parse(text, FilePath));
        if (edit is { } e)
        {
            Document.Replace(e.Offset, e.Length, e.Text);
            StatusMessage?.Invoke("Imports optimized");
        }
        else StatusMessage?.Invoke("Imports are already optimized");
        Analyze();
    }

    // ------------------------------------------------------------------ navigation & hover

    // ------------------------------------------------------------------ NeoPHP names in strings

    private bool _neoCompletion;
    private readonly NeoGutterMargin _neoGutter = new();

    private bool SupportsNeo => !IsPreview && Language is "PHP" or "Twig" or "YAML";

    /// <summary>NeoPHP reference at the offset (searched in its line only: cheap enough for every keystroke).</summary>
    public NeoRef? NeoRefAt(int offset)
    {
        if (!SupportsNeo || offset < 0 || offset > Document.TextLength) return null;
        var line = Document.GetLineByOffset(offset);
        var r = NeoInsight.At(Document.GetText(line), offset - line.Offset, Language);
        return r == null ? null : r with { Start = r.Start + line.Offset };
    }

    private (int start, List<CompletionEntry> entries)? NeoCompletionAt(int caret)
    {
        if (!SupportsNeo) return null;
        var line = Document.GetLineByOffset(caret);
        var r = NeoInsight.Complete(Document.GetText(line), caret - line.Offset, Language);
        return r == null ? null : (r.Value.start + line.Offset, r.Value.entries);
    }

    private string? NeoHover(int offset) => NeoRefAt(offset) is { } r ? NeoInsight.Describe(r) : null;

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || !(IsPhp || SupportsNeo)) return;
        var pos = GetPositionFromPoint(e.GetPosition(this));
        if (pos == null) return;
        var offset = Document.GetOffset(pos.Value.Location);
        if (GoToDeclaration(offset)) e.Handled = true;
    }

    // ------------------------------------------------------------------ colour swatches & picker

    private readonly ColorSwatchGenerator _colors = new();

    /// <summary>Opens the colour picker for a literal; the change is applied live, as one undo step (Esc cancels).</summary>
    private void OpenColorPicker(ColorLiteral literal, FrameworkElement swatch)
    {
        if (IsReadOnly) return;
        var anchor = Document.CreateAnchor(literal.Index);
        var length = literal.Length;
        var fileColors = new List<Color>();
        foreach (var line in Document.Lines.Take(5000))
            fileColors.AddRange(ColorLiterals.Find(Document.GetText(line), Language).Select(l => l.Color));
        fileColors.Remove(literal.Color);

        var picker = new Controls.ColorPicker(literal.Color, fileColors);
        var pos = swatch.TranslatePoint(new Point(0, swatch.ActualHeight + 2), TextArea.TextView);
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = picker, PlacementTarget = TextArea.TextView, Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = pos.X - 8, VerticalOffset = pos.Y, AllowsTransparency = true, StaysOpen = false,
        };
        bool changed = false, cancelled = false;
        Document.UndoStack.StartUndoGroup();
        picker.ColorChanged += c =>
        {
            var text = ColorLiterals.Format(c, literal.Format, literal.Text);
            Document.Replace(anchor.Offset, length, text);
            length = text.Length;
            changed = true;
        };
        picker.Accepted += () => popup.IsOpen = false;
        popup.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { cancelled = true; popup.IsOpen = false; e.Handled = true; } };
        popup.Closed += (_, _) =>
        {
            Document.UndoStack.EndUndoGroup();
            if (cancelled && changed) Undo();
            TextArea.Focus();
        };
        popup.IsOpen = true;
    }

    // ------------------------------------------------------------------ Ctrl+hover links

    private readonly LinkColorizer _link = new();
    private (int start, int end) _linkWord = (-1, -1);

    private void InitLinks()
    {
        TextArea.TextView.LineTransformers.Add(_link);
        PreviewMouseMove += (_, e) => UpdateLink(e.GetPosition(this));
        MouseLeave += (_, _) => ClearLink();
        PreviewKeyDown += (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl && !e.IsRepeat) UpdateLink(Mouse.GetPosition(this)); };
        PreviewKeyUp += (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl) ClearLink(); };
        LostKeyboardFocus += (_, _) => ClearLink();
    }

    private void UpdateLink(Point mouse)
    {
        if (!(IsPhp || SupportsNeo) || IsPreview || (Keyboard.Modifiers & ModifierKeys.Control) == 0) { ClearLink(); return; }
        var pos = GetPositionFromPoint(mouse);
        if (pos == null) { ClearLink(); return; }
        var offset = Document.GetOffset(pos.Value.Location);
        if (NeoRefAt(offset) is { } neo)
        {
            if ((neo.Start, neo.Start + neo.Length) == _linkWord) return;
            _linkWord = (neo.Start, neo.Start + neo.Length);
            var ok = NeoInsight.Resolve(neo) != null;
            _link.Start = ok ? neo.Start : -1;
            _link.Length = neo.Length;
            _link.Brush = ThemeManager.Brush("LinkFg");
            Cursor = TextArea.Cursor = ok ? Cursors.Hand : null;
            TextArea.TextView.Redraw();
            return;
        }
        if (TemplateSymbolAt(offset) is { File: not null } sym)
        {
            if ((sym.Start, sym.Start + sym.Length) == _linkWord) return;
            _linkWord = (sym.Start, sym.Start + sym.Length);
            _link.Start = sym.Start;
            _link.Length = sym.Length;
            _link.Brush = ThemeManager.Brush("LinkFg");
            Cursor = TextArea.Cursor = Cursors.Hand;
            TextArea.TextView.Redraw();
            return;
        }
        if (!IsPhp) { ClearLink(); return; }
        int s = offset, e = offset;
        while (s > 0 && (PhpLexer.IsIdentPart(Document.GetCharAt(s - 1)) || Document.GetCharAt(s - 1) == '\\')) s--;
        while (e < Document.TextLength && (PhpLexer.IsIdentPart(Document.GetCharAt(e)) || Document.GetCharAt(e) == '\\')) e++;
        if (s > 0 && Document.GetCharAt(s - 1) == '$') s--;
        if (e <= s) { ClearLink(); return; }
        if ((s, e) == _linkWord) return; // same word: keep the current state (resolution is not free)
        _linkWord = (s, e);
        SymbolTarget? target = null;
        try { target = Workspace.Insight.Resolve(Text, s + (Document.GetCharAt(s) == '$' ? 1 : 0), FilePath); } catch { }
        if (target == null || target.File == null && !target.Description.Contains("function") && !target.Description.Contains("class"))
        {
            _link.Start = -1;
            Cursor = null;
        }
        else
        {
            _link.Start = s;
            _link.Length = e - s;
            _link.Brush = ThemeManager.Brush("LinkFg");
            Cursor = Cursors.Hand;
            TextArea.Cursor = Cursors.Hand;
        }
        TextArea.TextView.Redraw();
    }

    private void ClearLink()
    {
        _linkWord = (-1, -1);
        if (_link.Start < 0 && TextArea.Cursor == null) return;
        _link.Start = -1;
        Cursor = null;
        TextArea.Cursor = null;
        TextArea.TextView.Redraw();
    }

    public bool GoToDeclaration(int offset)
    {
        if (NeoRefAt(offset) is { } neo)
        {
            if (NeoInsight.Resolve(neo) is { } t) NavigateRequested?.Invoke(t.File, t.Offset);
            else StatusMessage?.Invoke(NeoInsight.Problem(neo with { Checked = true }) ?? $"Cannot find '{neo.Value}'");
            return true;
        }
        if (TemplateSymbolAt(offset) is { File: not null } sym)
        {
            NavigateRequested?.Invoke(sym.File, sym.Offset);
            return true;
        }
        if (!IsPhp) return false;
        SymbolTarget? target;
        try { target = Workspace.Insight.Resolve(Text, offset, FilePath); } catch { return false; }
        if (target == null) { StatusMessage?.Invoke("Cannot find declaration to go to"); return false; }
        if (target.File == null) { StatusMessage?.Invoke("Built-in PHP symbol: " + target.Description.Split('\n')[0]); return true; }
        NavigateRequested?.Invoke(target.File, target.Offset);
        return true;
    }

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        var pos = GetPositionFromPoint(e.GetPosition(this));
        if (pos == null) return;
        var offset = Document.GetOffset(pos.Value.Location);

        var hits = _squiggles.Markers.Where(m => offset >= m.Start && offset <= m.Start + m.Length).OrderBy(m => m.Severity).ToList();
        string? text = hits.Count > 0 ? string.Join("\n\n", hits.Select(m => m.Message).Distinct()) : null;
        if (text == null)
        {
            try { text = NeoHover(offset) ?? TemplateSymbolAt(offset)?.Description; } catch { }
        }
        if (text == null && IsPhp)
        {
            try { text = Workspace.Insight.Resolve(Text, offset, FilePath)?.Description; } catch { }
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        _hoverTip.Child = DocPopup.Build(text, hits.Count > 0);
        var p = e.GetPosition(this);
        _hoverTip.PlacementTarget = this;
        _hoverTip.HorizontalOffset = p.X - 8;
        _hoverTip.VerticalOffset = p.Y + 14;
        _hoverTip.IsOpen = true;
        e.Handled = true;
    }

    public void GoToOffset(int offset)
    {
        offset = Math.Clamp(offset, 0, Document.TextLength);
        CaretOffset = offset;
        var loc = Document.GetLocation(offset);
        ScrollTo(loc.Line, loc.Column);
        TextArea.Focus();
        Dispatcher.BeginInvoke(() =>
        {
            var line = Document.GetLineByOffset(offset);
            ScrollToVerticalOffset(Math.Max(0, TextArea.TextView.GetVisualTopByDocumentLine(line.LineNumber) - ActualHeight / 3));
        }, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ line editing (PhpStorm keymap)

    private void DuplicateLine()
    {
        if (!TextArea.Selection.IsEmpty)
        {
            var sel = TextArea.Selection.GetText();
            var end = SelectionStart + SelectionLength;
            Document.Insert(end, sel);
            Select(end, sel.Length);
            return;
        }
        var line = Document.GetLineByOffset(CaretOffset);
        var col = CaretOffset - line.Offset;
        var text = Document.GetText(line);
        var nl = Document.Text.Contains("\r\n") ? "\r\n" : "\n";
        Document.Insert(line.EndOffset, nl + text);
        CaretOffset = line.NextLine!.Offset + col;
    }

    private void DeleteLine()
    {
        var line = Document.GetLineByOffset(CaretOffset);
        Document.Remove(line.Offset, line.TotalLength);
    }

    private void MoveLine(int dir)
    {
        var line = Document.GetLineByOffset(CaretOffset);
        var col = CaretOffset - line.Offset;
        var other = dir < 0 ? line.PreviousLine : line.NextLine;
        if (other == null) return;
        var a = Document.GetText(line);
        var b = Document.GetText(other);
        Document.BeginUpdate();
        if (dir < 0)
        {
            Document.Replace(line.Offset, line.Length, b);
            Document.Replace(other.Offset, other.Length, a);
            CaretOffset = other.Offset + Math.Min(col, a.Length);
        }
        else
        {
            Document.Replace(other.Offset, other.Length, a);
            Document.Replace(line.Offset, line.Length, b);
            var next = Document.GetLineByOffset(line.Offset).NextLine!;
            CaretOffset = next.Offset + Math.Min(col, a.Length);
        }
        Document.EndUpdate();
    }

    private void ToggleComment()
    {
        var prefix = Language switch
        {
            "YAML" or "Shell" or "ENV" or "Ignore" => "# ",
            "SQL" => "-- ",
            "HTML" or "XML" or "Markdown" => null,
            "Twig" => null,
            _ => "// ",
        };
        var startLine = Document.GetLineByOffset(SelectionStart);
        var endLine = Document.GetLineByOffset(SelectionStart + SelectionLength);
        if (prefix == null)
        {
            var (open, close) = Language == "Twig" ? ("{# ", " #}") : ("<!-- ", " -->");
            var s = startLine.Offset;
            var e = endLine.EndOffset;
            var t = Document.GetText(s, e - s);
            var trimmed = t.Trim();
            if (trimmed.StartsWith(open.Trim()) && trimmed.EndsWith(close.Trim()))
                Document.Replace(s, e - s, t.Replace(open, "").Replace(open.Trim(), "").Replace(close, "").Replace(close.Trim(), ""));
            else Document.Replace(s, e - s, LeadingWhitespace(t) + open + t.TrimStart() + close);
            return;
        }
        var lines = new List<DocumentLine>();
        for (var l = startLine; l != null && l.LineNumber <= endLine.LineNumber; l = l.NextLine) lines.Add(l);
        bool allCommented = lines.Where(l => Document.GetText(l).Trim().Length > 0)
            .All(l => Document.GetText(l).TrimStart().StartsWith(prefix.Trim()));
        Document.BeginUpdate();
        foreach (var l in lines.AsEnumerable().Reverse())
        {
            var t = Document.GetText(l);
            if (t.Trim().Length == 0) continue;
            var ws = LeadingWhitespace(t).Length;
            if (allCommented)
            {
                var rest = t[ws..];
                var len = rest.StartsWith(prefix) ? prefix.Length : prefix.Trim().Length;
                Document.Remove(l.Offset + ws, len);
            }
            else Document.Insert(l.Offset + (lines.Count > 1 ? 0 : ws), prefix);
        }
        Document.EndUpdate();
        if (lines.Count == 1 && startLine.NextLine != null && TextArea.Selection.IsEmpty)
            CaretOffset = startLine.NextLine.Offset + Math.Min(CaretOffset - startLine.Offset, startLine.NextLine.Length);
    }
}

/// <summary>Keeps the previous indentation, indents after { [ ( and dedents closing brackets.</summary>
public sealed class SmartIndentation(bool yaml) : IIndentationStrategy
{
    public void IndentLine(TextDocument document, DocumentLine line)
    {
        var prev = line.PreviousLine;
        if (prev == null) return;
        // find previous non-empty line
        while (prev != null && document.GetText(prev).Trim().Length == 0) prev = prev.PreviousLine;
        if (prev == null) return;
        var prevText = document.GetText(prev);
        var indent = new string(prevText.TakeWhile(c => c is ' ' or '\t').ToArray());
        var trimmed = prevText.TrimEnd();
        var unit = "    ";
        if (trimmed.EndsWith("{") || trimmed.EndsWith("[") || trimmed.EndsWith("(") || yaml && trimmed.EndsWith(":")
            || trimmed.EndsWith("=>") || Regex.IsMatch(trimmed, @"<(?!/)(?![^>]*/>)[\w:-]+[^>]*>$") && !Regex.IsMatch(trimmed, @"<(br|img|input|meta|link|hr)\b", RegexOptions.IgnoreCase))
            indent += unit;
        var curText = document.GetText(line);
        var curTrim = curText.TrimStart();
        if (curTrim.StartsWith("}") || curTrim.StartsWith("]") || curTrim.StartsWith(")") || curTrim.StartsWith("</"))
            indent = indent.Length >= unit.Length ? indent[..^unit.Length] : "";
        var currentIndentLen = curText.Length - curTrim.Length;
        document.Replace(line.Offset, currentIndentLen, indent);
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
        for (int i = beginLine; i <= endLine; i++) IndentLine(document, document.GetLineByNumber(i));
    }
}
