using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NeoIDE.Services;

namespace NeoIDE.Controls;

/// <summary>Native Markdown preview (CommonMark subset + GitHub tables / fenced code), coloured with the IDE theme.</summary>
public sealed class MarkdownView : FlowDocumentScrollViewer
{
    public string? BaseDirectory { get; set; }

    public MarkdownView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        IsToolBarVisible = false;
        Focusable = true;
    }

    public void Render(string markdown)
    {
        var offset = FindScrollViewer()?.VerticalOffset ?? 0;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14.5, LineHeight = 23,
            PagePadding = new Thickness(36, 22, 36, 40), TextAlignment = TextAlignment.Left,
            Background = ThemeManager.Brush("EditorBg"), Foreground = ThemeManager.Brush("Fg"),
        };
        foreach (var block in ParseBlocks(markdown.Replace("\r\n", "\n").Split('\n'))) doc.Blocks.Add(block);
        Document = doc;
        Dispatcher.BeginInvoke(() => FindScrollViewer()?.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public ScrollViewer? FindScrollViewer()
    {
        if (VisualTreeHelper.GetChildrenCount(this) == 0) return null;
        DependencyObject d = this;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(d);
        while (queue.Count > 0)
        {
            var x = queue.Dequeue();
            if (x is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(x); i++) queue.Enqueue(VisualTreeHelper.GetChild(x, i));
        }
        return null;
    }

    // ------------------------------------------------------------------ blocks

    private IEnumerable<Block> ParseBlocks(string[] lines)
    {
        int i = 0;
        var para = new List<string>();
        Block? FlushPara()
        {
            if (para.Count == 0) return null;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            AddInlines(p.Inlines, string.Join(" ", para.Select(l => l.Trim())));
            para.Clear();
            return p;
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // fenced code
            var fence = Regex.Match(trimmed, @"^(```|~~~)\s*(\w*)");
            if (fence.Success)
            {
                if (FlushPara() is { } fp) yield return fp;
                var marker = fence.Groups[1].Value;
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith(marker)) code.Add(lines[i++]);
                i++;
                yield return CodeBlock(string.Join("\n", code), fence.Groups[2].Value);
                continue;
            }
            if (trimmed.Length == 0) { if (FlushPara() is { } fp) yield return fp; i++; continue; }

            // headings
            var h = Regex.Match(trimmed, @"^(#{1,6})\s+(.*?)\s*#*$");
            if (h.Success)
            {
                if (FlushPara() is { } fp) yield return fp;
                yield return Heading(h.Groups[1].Value.Length, h.Groups[2].Value);
                i++;
                continue;
            }
            // setext headings
            if (i + 1 < lines.Length && para.Count == 0 && Regex.IsMatch(lines[i + 1].Trim(), @"^(=+|-+)$") && !Regex.IsMatch(trimmed, @"^[-*+]\s"))
            {
                yield return Heading(lines[i + 1].Trim()[0] == '=' ? 1 : 2, trimmed);
                i += 2;
                continue;
            }
            // horizontal rule
            if (Regex.IsMatch(trimmed, @"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$"))
            {
                if (FlushPara() is { } fp) yield return fp;
                yield return new BlockUIContainer(new Border { Height = 1, Background = ThemeManager.Brush("SubtleBorder"), Margin = new Thickness(0, 8, 0, 8) });
                i++;
                continue;
            }
            // block quote
            if (trimmed.StartsWith(">"))
            {
                if (FlushPara() is { } fp) yield return fp;
                var quote = new List<string>();
                while (i < lines.Length && lines[i].Trim().StartsWith(">")) quote.Add(Regex.Replace(lines[i++].Trim(), @"^>\s?", ""));
                var section = new Section
                {
                    BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(14, 2, 0, 2), Margin = new Thickness(0, 0, 0, 12), Foreground = ThemeManager.Brush("FgSecondary"),
                };
                foreach (var b in ParseBlocks(quote.ToArray())) section.Blocks.Add(b);
                yield return section;
                continue;
            }
            // table
            if (trimmed.StartsWith("|") && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}"))
            {
                if (FlushPara() is { } fp) yield return fp;
                var rows = new List<string> { trimmed };
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith("|")) rows.Add(lines[i++].Trim());
                yield return Table(rows);
                continue;
            }
            // lists
            var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (li.Success)
            {
                if (FlushPara() is { } fp) yield return fp;
                yield return ListBlock(lines, ref i, li.Groups[1].Value.Length);
                continue;
            }
            para.Add(line);
            i++;
        }
        if (FlushPara() is { } last) yield return last;
    }

    private Block Heading(int level, string text)
    {
        var sizes = new[] { 28.0, 23, 19, 16.5, 15, 14 };
        var p = new Paragraph
        {
            FontSize = sizes[level - 1], FontWeight = level <= 2 ? FontWeights.SemiBold : FontWeights.Bold,
            Margin = new Thickness(0, level <= 2 ? 18 : 12, 0, 8),
        };
        AddInlines(p.Inlines, text);
        if (level > 2) return p;
        var section = new Section { BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(0, 0, 0, 4) };
        p.Margin = new Thickness(0, level == 1 ? 6 : 14, 0, 0);
        section.Blocks.Add(p);
        return section;
    }

    private static Block CodeBlock(string code, string language)
    {
        var p = new Paragraph(new Run(code))
        {
            FontFamily = DisplayManager.EditorFont, FontSize = 13, LineHeight = 19,
            Background = ThemeManager.Brush("PanelBg"), Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 14), BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(1),
        };
        if (language.Length > 0) p.ToolTip = language;
        return p;
    }

    private Block Table(List<string> rows)
    {
        string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var header = Cells(rows[0]);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 14), BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(1, 1, 0, 0) };
        foreach (var _ in header) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < rows.Count; r++)
        {
            var cells = r == 0 ? header : Cells(rows[r]);
            var row = new TableRow();
            if (r == 0) row.Background = ThemeManager.Brush("PanelBg");
            for (int c = 0; c < header.Length; c++)
            {
                var para = new Paragraph { Margin = new Thickness(0) };
                if (r == 0) para.FontWeight = FontWeights.SemiBold;
                AddInlines(para.Inlines, c < cells.Length ? cells[c] : "");
                row.Cells.Add(new TableCell(para) { Padding = new Thickness(10, 5, 10, 5), BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(0, 0, 1, 1) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private Block ListBlock(string[] lines, ref int i, int indent)
    {
        var first = Regex.Match(lines[i], @"^\s*([-*+]|\d+[.)])\s");
        var ordered = char.IsDigit(first.Groups[1].Value[0]);
        var list = new List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(24, 0, 0, 0) };
        while (i < lines.Length)
        {
            var m = Regex.Match(lines[i], @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (!m.Success || m.Groups[1].Value.Length < indent) break;
            if (m.Groups[1].Value.Length > indent)
            {
                // nested list inside the previous item
                var nested = ListBlock(lines, ref i, m.Groups[1].Value.Length);
                if (list.ListItems.LastListItem is { } prevItem) prevItem.Blocks.Add(nested);
                continue;
            }
            var text = m.Groups[3].Value;
            var item = new ListItem();
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            var task = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
            if (task.Success)
            {
                p.Inlines.Add(new Run(task.Groups[1].Value.Trim().Length > 0 ? "☑ " : "☐ ") { Foreground = ThemeManager.Brush("Accent") });
                text = task.Groups[2].Value;
            }
            AddInlines(p.Inlines, text);
            item.Blocks.Add(p);
            list.ListItems.Add(item);
            i++;
            // continuation lines of the item
            while (i < lines.Length && lines[i].Trim().Length > 0 && !Regex.IsMatch(lines[i], @"^\s*([-*+]|\d+[.)])\s") && lines[i].StartsWith(new string(' ', indent + 2)))
                AddInlines(p.Inlines, " " + lines[i++].Trim());
        }
        return list;
    }

    // ------------------------------------------------------------------ inlines

    private static readonly Regex InlineRe = new(
        @"(?<code>`+)(?<codetext>.+?)\k<code>" +
        @"|!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)(\s+""[^""]*"")?\)" +
        @"|\[(?<ltext>[^\]]+)\]\((?<href>[^)\s]+)(\s+""[^""]*"")?\)" +
        @"|<(?<auto>https?://[^>]+)>" +
        @"|(?<bold>\*\*|__)(?<btext>.+?)\k<bold>" +
        @"|(?<!\w)(?<it>[*_])(?<itext>[^*_\s][^*_]*?)\k<it>(?!\w)" +
        @"|~~(?<strike>.+?)~~" +
        @"|(?<url>https?://[^\s<>()]+[^\s<>().,;:!?])",
        RegexOptions.Compiled);

    private void AddInlines(InlineCollection target, string text, bool insideLink = false)
    {
        int pos = 0;
        foreach (Match m in InlineRe.Matches(text))
        {
            // inside a link: no nested links / images (a bare URL as link text would recurse forever)
            if (insideLink && (m.Groups["href"].Success || m.Groups["auto"].Success || m.Groups["url"].Success || m.Groups["src"].Success)) continue;
            if (m.Index > pos) target.Add(new Run(text[pos..m.Index]));
            pos = m.Index + m.Length;
            if (m.Groups["codetext"].Success)
                target.Add(new Run(m.Groups["codetext"].Value.Trim())
                {
                    FontFamily = DisplayManager.EditorFont, FontSize = 13, Background = ThemeManager.Brush("PanelBg"), Foreground = ThemeManager.Brush("Fg"),
                });
            else if (m.Groups["src"].Success) target.Add(ImageInline(m.Groups["src"].Value, m.Groups["alt"].Value));
            else if (m.Groups["href"].Success) target.Add(Link(m.Groups["ltext"].Value, m.Groups["href"].Value));
            else if (m.Groups["auto"].Success) target.Add(Link(m.Groups["auto"].Value, m.Groups["auto"].Value));
            else if (m.Groups["url"].Success) target.Add(Link(m.Groups["url"].Value, m.Groups["url"].Value));
            else if (m.Groups["btext"].Success) { var b = new Bold(); AddInlines(b.Inlines, m.Groups["btext"].Value, insideLink); target.Add(b); }
            else if (m.Groups["itext"].Success) { var it = new Italic(); AddInlines(it.Inlines, m.Groups["itext"].Value, insideLink); target.Add(it); }
            else if (m.Groups["strike"].Success) target.Add(new Run(m.Groups["strike"].Value) { TextDecorations = TextDecorations.Strikethrough });
        }
        if (pos < text.Length) target.Add(new Run(text[pos..]));
    }

    private Inline Link(string text, string href)
    {
        var link = new Hyperlink { Foreground = ThemeManager.Brush("LinkFg"), TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = href };
        AddInlines(link.Inlines, text, insideLink: true);
        link.Click += (_, _) =>
        {
            try
            {
                var target = href.StartsWith("http") || BaseDirectory == null ? href : Path.GetFullPath(Path.Combine(BaseDirectory, href.Split('#')[0]));
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch { }
        };
        return link;
    }

    private Inline ImageInline(string src, string alt)
    {
        try
        {
            Uri uri = src.StartsWith("http") ? new Uri(src) : new Uri(Path.GetFullPath(Path.Combine(BaseDirectory ?? "", src)));
            if (uri.IsFile && !File.Exists(uri.LocalPath)) return new Run($"[{alt}]") { Foreground = ThemeManager.Brush("FgSecondary") };
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            return new InlineUIContainer(new Image { Source = bmp, MaxWidth = 700, Stretch = Stretch.Uniform, ToolTip = alt });
        }
        catch { return new Run($"[{alt}]") { Foreground = ThemeManager.Brush("FgSecondary") }; }
    }
}
