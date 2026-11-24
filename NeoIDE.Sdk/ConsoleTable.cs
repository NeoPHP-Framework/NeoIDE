namespace NeoIDE.Sdk;

/// <summary>Parses the tables printed by the bin/neo commands (route:list, migration:status, queue:status…).</summary>
public static class ConsoleTable
{
    /// <summary>Every table of the output; each row maps the header names to the cell values.</summary>
    public static List<List<Dictionary<string, string>>> ParseAll(string output)
    {
        var tables = new List<List<Dictionary<string, string>>>();
        var lines = output.Replace("\r", "").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('+') && t.Contains("--")) i = ParseBox(lines, i, tables);
            else if (IsDashRuler(t)) i = ParseRuler(lines, i, tables);
            else i++;
        }
        return tables;
    }

    /// <summary>The first table of the output (empty when none).</summary>
    public static List<Dictionary<string, string>> Parse(string output) => ParseAll(output).FirstOrDefault() ?? new();

    // +------+--------+
    // | Name | Method |
    // +------+--------+
    // | home | GET    |
    // +------+--------+
    private static int ParseBox(string[] lines, int i, List<List<Dictionary<string, string>>> tables)
    {
        string[]? header = null;
        var rows = new List<Dictionary<string, string>>();
        Dictionary<string, string>? last = null;
        for (; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('+')) continue;
            if (!t.StartsWith('|')) break;
            var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (header == null) { header = cells; continue; }
            // continuation line of a multi-line cell: first cell empty
            if (last != null && cells.Length > 0 && cells[0].Length == 0 && cells.Skip(1).Any(c => c.Length > 0) && header[0].Length > 0)
            {
                for (int c = 0; c < Math.Min(cells.Length, header.Length); c++)
                    if (cells[c].Length > 0) last[header[c]] = (last[header[c]] + " " + cells[c]).Trim();
                continue;
            }
            last = Row(header, cells);
            rows.Add(last);
        }
        if (header != null) tables.Add(rows);
        return i;
    }

    private static bool IsDashRuler(string t) => t.Length >= 3 && t.All(c => c == '-' || c == ' ') && t.Contains("---");

    //  ------ --------
    //   Name   Method
    //  ------ --------
    //   home   GET
    //  ------ --------
    private static int ParseRuler(string[] lines, int i, List<List<Dictionary<string, string>>> tables)
    {
        var ruler = lines[i];
        var cols = new List<(int start, int end)>();
        for (int c = 0; c < ruler.Length;)
        {
            if (ruler[c] != '-') { c++; continue; }
            var s = c;
            while (c < ruler.Length && ruler[c] == '-') c++;
            cols.Add((s, c));
        }
        i++;
        if (i >= lines.Length || cols.Count == 0) return i;
        var header = Cut(lines[i], cols);
        i++;
        if (i < lines.Length && IsDashRuler(lines[i].Trim())) i++;
        var rows = new List<Dictionary<string, string>>();
        for (; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (IsDashRuler(t)) { i++; break; }
            if (t.Length == 0) break;
            rows.Add(Row(header, Cut(lines[i], cols)));
        }
        tables.Add(rows);
        return i;
    }

    private static string[] Cut(string line, List<(int start, int end)> cols)
    {
        var cells = new string[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            var start = Math.Min(cols[c].start, line.Length);
            var end = c == cols.Count - 1 ? line.Length : Math.Min(cols[c + 1].start, line.Length);
            cells[c] = line[start..Math.Max(start, end)].Trim();
        }
        return cells;
    }

    private static Dictionary<string, string> Row(string[] header, string[] cells)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < header.Length; c++)
        {
            var key = header[c].Length > 0 ? header[c] : $"#{c}";
            row[key] = c < cells.Length ? cells[c] : "";
        }
        return row;
    }
}
