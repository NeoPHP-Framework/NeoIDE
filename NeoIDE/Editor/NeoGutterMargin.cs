using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>
/// NeoPHP gutter icons: the route of a controller action (open in the browser, copy the URL), the template rendered
/// by render() (open it), the controllers rendering a template, the event of a listener / the message of a handler.
/// </summary>
public sealed class NeoGutterMargin : AbstractMargin
{
    public sealed record Item(int Line, string Glyph, string ColorKey, string ToolTip, Action<FrameworkElement> OnClick);

    private List<Item> _items = new();
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public NeoGutterMargin()
    {
        Cursor = Cursors.Hand;
        ToolTip = "";
        ToolTipService.SetInitialShowDelay(this, 200);
        ToolTipOpening += OnToolTipOpening;
    }

    protected override Size MeasureOverride(Size availableSize) => new(_items.Count > 0 ? 16 : 0, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= Redraw;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += Redraw;
    }

    private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Recomputes the icons of the editor's file.</summary>
    public void Update(CodeEditor editor)
    {
        var items = new List<Item>();
        try { if (editor.FilePath != null && !editor.IsPreview && Workspace.Root != null) Compute(editor, items); }
        catch { }
        var hadItems = _items.Count > 0;
        _items = items;
        if (hadItems != items.Count > 0) InvalidateMeasure();
        InvalidateVisual();
    }

    private static void Compute(CodeEditor editor, List<Item> items)
    {
        var path = Path.GetFullPath(editor.FilePath!);
        var text = editor.Document.Text;
        int LineOf(int offset) => editor.Document.GetLineByOffset(Math.Clamp(offset, 0, editor.Document.TextLength)).LineNumber;

        if (editor.Language == "PHP")
        {
            // routes of the controller actions of this file (lines come from the buffer: the index may be a few seconds old)
            var routes = NeoProject.Routes.Where(r => string.Equals(r.File, path, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var r in routes)
            {
                var method = r.Controller?.Split("::").LastOrDefault();
                var m = method == null ? null : Regex.Match(text, @"function\s+" + Regex.Escape(method) + @"\s*\(");
                var line = m is { Success: true } ? AttributeLineAbove(editor, LineOf(m.Index)) : r.Line;
                var route = r;
                items.Add(new Item(line, "", "Accent", $"{r.Methods} {r.Path}" + (r.Name.Length > 0 ? $"  ({r.Name})" : "") + "\nClick: open in the browser, copy the URL",
                    anchor => RouteMenu(route, anchor)));
            }

            // templates rendered
            foreach (var reference in NeoInsight.All(text, "PHP").Where(x => x.Kind == NeoRefKind.Template))
            {
                var file = NeoProject.Template(reference.Value);
                if (file == null) continue;
                var name = reference.Value;
                items.Add(new Item(LineOf(reference.Start), "", "LinkFg", $"Template {name}\n{Rel(file)}\nClick: open it", _ => Ide?.Invoke(file, 0)));
            }

            // listener → event, message handler → message
            foreach (Match a in Regex.Matches(text, @"#\[\s*(?<attr>AsListener|AsMessageHandler)\b(?<args>[^\]]*)\]"))
            {
                var info = PhpParser.Parse(text, path);
                string? target = null;
                var explicitClass = Regex.Match(a.Groups["args"].Value, @"(?<c>\\?[A-Za-z_][\w\\]*)::class");
                if (explicitClass.Success) target = info.ResolveClass(explicitClass.Groups["c"].Value);
                else
                {
                    var method = Regex.Match(a.Groups["args"].Value, @"method\s*:\s*['""](\w+)").Groups[1].Value;
                    var fn = Regex.Match(text[a.Index..], @"function\s+" + (method.Length > 0 ? Regex.Escape(method) : @"(?:__invoke|\w+)") + @"\s*\(\s*(?<t>\\?[A-Za-z_][\w\\]*)\s+\$");
                    if (fn.Success) target = info.ResolveClass(fn.Groups["t"].Value);
                }
                if (target == null) continue;
                var cls = Workspace.Index.GetClass(target);
                var label = a.Groups["attr"].Value == "AsListener" ? "Listens to" : "Handles";
                items.Add(new Item(LineOf(a.Index), a.Groups["attr"].Value == "AsListener" ? "" : "", "Accent",
                    $"{label} {target}" + (cls?.File == null ? " (not found)" : "\nClick: go to the class"),
                    _ => { if (cls?.File != null) Ide?.Invoke(cls.File, cls.Offset); }));
            }
        }

        // a template: the controllers that render it
        if (NeoProject.TemplateNameOf(path) is { } templateName)
            items.Add(new Item(1, "", "Accent", $"Template '{templateName}'\nClick: controllers rendering it", anchor => RenderedByMenu(path, anchor)));
    }

    /// <summary>The attribute lines above a method: the icon goes on the #[Route] line, like PhpStorm.</summary>
    private static int AttributeLineAbove(CodeEditor editor, int line)
    {
        var l = line;
        while (l > 1)
        {
            var prev = editor.Document.GetText(editor.Document.GetLineByNumber(l - 1)).Trim();
            if (prev.StartsWith("#[") && prev.Contains("Route")) return l - 1;
            if (!prev.StartsWith("#[") && !prev.StartsWith("*") && !prev.StartsWith("/**")) break;
            l--;
        }
        return line;
    }

    /// <summary>Opens a file at an offset (set by the main window).</summary>
    public static Action<string, int>? Ide { get; set; }

    private static string Rel(string file) => Workspace.Root != null ? Path.GetRelativePath(Workspace.Root, file).Replace('\\', '/') : file;

    private static string BaseUrl()
    {
        var url = NeoProject.Env("APP_URL") is { Count: > 0 } d ? d[^1].Value : $"http://127.0.0.1:{Workspace.Settings.ServerPort}";
        return url.TrimEnd('/');
    }

    private static void RouteMenu(NeoRoute r, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var url = BaseUrl() + r.Path;
        var hasParams = r.Path.Contains('{');
        void Add(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header.Literal(), IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        menu.Items.Add(new MenuItem { Header = $"{r.Methods} {r.Path}".Literal(), IsEnabled = false, FontWeight = FontWeights.SemiBold });
        Add(hasParams ? "Open in browser (fill the {parameters} first)" : "Open in browser", () =>
        {
            var target = url;
            foreach (Match p in Regex.Matches(r.Path, @"\{(\w+)[^}]*\}"))
            {
                var v = Dialogs.InputDialog.Ask("Open route", $"Value of {{{p.Groups[1].Value}}}:", "1");
                if (v == null) return;
                target = target.Replace(p.Value, Uri.EscapeDataString(v));
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }, r.Methods is "ANY" || r.Methods.Contains("GET"));
        Add("Copy URL", () => Clipboard.SetText(url));
        if (r.Name.Length > 0) Add($"Copy route name '{r.Name}'", () => Clipboard.SetText(r.Name));
        menu.IsOpen = true;
    }

    private static void RenderedByMenu(string templateFile, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var src = Path.Combine(Workspace.Root!, "src");
        var hits = new List<(string file, int offset, string label)>();
        if (Directory.Exists(src))
            foreach (var f in Directory.EnumerateFiles(src, "*.php", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }
                if (!text.Contains("render") && !text.Contains("include") && !text.Contains("extend")) continue;
                foreach (var r in NeoInsight.All(text, "PHP").Where(x => x.Kind == NeoRefKind.Template))
                    if (string.Equals(NeoProject.Template(r.Value), templateFile, StringComparison.OrdinalIgnoreCase))
                    {
                        var line = text[..r.Start].Count(c => c == '\n') + 1;
                        var fn = Regex.Matches(text[..r.Start], @"function\s+(\w+)").LastOrDefault()?.Groups[1].Value;
                        hits.Add((f, r.Start, $"{Path.GetFileNameWithoutExtension(f)}{(fn != null ? "::" + fn + "()" : "")}   {Rel(f)}:{line}"));
                    }
            }
        // templates extending / including it
        foreach (var t in NeoProject.TemplateNames.Select(NeoProject.Template).Where(f => f != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = File.ReadAllText(t!); } catch { continue; }
            var lang = t!.EndsWith(".twig", StringComparison.OrdinalIgnoreCase) ? "Twig" : "PHP";
            foreach (var r in NeoInsight.All(text, lang).Where(x => x.Kind == NeoRefKind.Template))
                if (string.Equals(NeoProject.Template(r.Value), templateFile, StringComparison.OrdinalIgnoreCase))
                    hits.Add((t, r.Start, $"{NeoProject.TemplateNameOf(t)}   (template)"));
        }
        if (hits.Count == 0) menu.Items.Add(new MenuItem { Header = "Not rendered by the controllers of src/", IsEnabled = false });
        foreach (var (file, offset, label) in hits.Take(30))
        {
            var mi = new MenuItem { Header = label.Literal() };
            mi.Click += (_, _) => Ide?.Invoke(file, offset);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        if (view == null || !view.VisualLinesValid || _items.Count == 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var vl in view.VisualLines)
        {
            var line = vl.FirstDocumentLine.LineNumber;
            var item = _items.FirstOrDefault(i => i.Line == line);
            if (item == null) continue;
            var ft = new FormattedText(item.Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11, ThemeManager.Brush(item.ColorKey), dpi);
            var top = vl.VisualTop - view.VerticalOffset + (vl.Height - ft.Height) / 2;
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, top));
        }
    }

    private Item? ItemAt(Point p)
    {
        var view = TextView;
        if (view == null) return null;
        var vl = view.GetVisualLineFromVisualTop(p.Y + view.VerticalOffset);
        return vl == null ? null : _items.FirstOrDefault(i => i.Line == vl.FirstDocumentLine.LineNumber);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (ItemAt(e.GetPosition(this)) is { } item) { item.OnClick(this); e.Handled = true; }
        else base.OnMouseLeftButtonDown(e);
    }

    private void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (ItemAt(Mouse.GetPosition(this)) is { } item) ToolTip = item.ToolTip;
        else e.Handled = true;
    }
}
