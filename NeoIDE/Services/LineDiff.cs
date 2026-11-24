namespace NeoIDE.Services;

/// <summary>A changed region: lines [AStart, AStart+ALength) of the old text replaced by [BStart, BStart+BLength) of the new one (0-based).</summary>
public readonly record struct DiffHunk(int AStart, int ALength, int BStart, int BLength)
{
    public bool IsInsert => ALength == 0;
    public bool IsDelete => BLength == 0;
}

/// <summary>Line based diff (common prefix/suffix + LCS on the middle part).</summary>
public static class LineDiff
{
    public static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    public static List<DiffHunk> Compute(string oldText, string newText) => Compute(Lines(oldText), Lines(newText));

    public static List<DiffHunk> Compute(string[] a, string[] b)
    {
        var hunks = new List<DiffHunk>();
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        int n = endA - start, m = endB - start;
        if (n == 0 && m == 0) return hunks;
        if (n == 0 || m == 0 || (long)n * m > 6_000_000)
        {
            hunks.Add(new DiffHunk(start, n, start, m));
            return hunks;
        }

        // LCS table on the middle part (hash lines first)
        var ha = new int[n];
        var hb = new int[m];
        var ids = new Dictionary<string, int>();
        int Id(string s) { if (!ids.TryGetValue(s, out var id)) ids[s] = id = ids.Count; return id; }
        for (int i = 0; i < n; i++) ha[i] = Id(a[start + i]);
        for (int j = 0; j < m; j++) hb[j] = Id(b[start + j]);
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = ha[i] == hb[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0, hs = -1, ht = -1;
        void Flush(int i, int j)
        {
            if (hs >= 0) { hunks.Add(new DiffHunk(start + hs, i - hs, start + ht, j - ht)); hs = ht = -1; }
        }
        while (x < n && y < m)
        {
            if (ha[x] == hb[y]) { Flush(x, y); x++; y++; }
            else
            {
                if (hs < 0) { hs = x; ht = y; }
                if (lcs[x + 1, y] >= lcs[x, y + 1]) x++; else y++;
            }
        }
        if (x < n || y < m) { if (hs < 0) { hs = x; ht = y; } }
        Flush(n, m);
        return hunks;
    }
}
