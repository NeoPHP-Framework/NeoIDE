namespace NeoIDE.Php;

public enum TokKind
{
    InlineHtml, OpenTag, CloseTag, Whitespace, Comment, DocComment,
    Variable, Identifier, String, Number, Punct, Attribute,
}

public readonly record struct Token(TokKind Kind, string Text, int Start)
{
    public int End => Start + Text.Length;
    public bool Is(string punct) => Kind == TokKind.Punct && Text == punct;
    public bool IsWord(string word) => Kind == TokKind.Identifier && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsTrivia => Kind is TokKind.Whitespace or TokKind.Comment or TokKind.DocComment or TokKind.InlineHtml or TokKind.OpenTag or TokKind.CloseTag;
    public override string ToString() => $"{Kind}:{Text}";
}

/// <summary>Small, error tolerant PHP tokenizer (good enough for indexing and completion).</summary>
public static class PhpLexer
{
    private static readonly string[] Puncts3 = { "?->", "...", "<=>", "**=", "===", "!==", "<<=", ">>=", "??=" };
    private static readonly string[] Puncts2 =
    {
        "->", "::", "=>", "??", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=",
        ".=", "%=", "&=", "|=", "^=", "<<", ">>", "**", "<>", "#[",
    };

    public static List<Token> Tokenize(string text, int length = -1, bool startInPhp = false)
    {
        var tokens = new List<Token>(Math.Max(16, text.Length / 4));
        int n = length < 0 ? text.Length : Math.Min(length, text.Length);
        int i = 0;
        bool php = startInPhp;

        while (i < n)
        {
            if (!php)
            {
                int open = text.IndexOf("<?", i, n - i, StringComparison.Ordinal);
                if (open < 0) { tokens.Add(new Token(TokKind.InlineHtml, text[i..n], i)); break; }
                if (open > i) tokens.Add(new Token(TokKind.InlineHtml, text[i..open], i));
                int tagLen = 2;
                if (string.Compare(text, open, "<?php", 0, 5, StringComparison.OrdinalIgnoreCase) == 0 && open + 5 <= n) tagLen = 5;
                else if (open + 2 < n && text[open + 2] == '=') tagLen = 3;
                tokens.Add(new Token(TokKind.OpenTag, text.Substring(open, Math.Min(tagLen, n - open)), open));
                i = open + tagLen;
                php = true;
                continue;
            }

            char c = text[i];
            int start = i;

            if (char.IsWhiteSpace(c))
            {
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                tokens.Add(new Token(TokKind.Whitespace, text[start..i], start));
                continue;
            }

            if (c == '?' && i + 1 < n && text[i + 1] == '>')
            {
                tokens.Add(new Token(TokKind.CloseTag, "?>", i));
                i += 2;
                php = false;
                continue;
            }

            // Comments
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int from = i + 2;
                int end = from <= n ? text.IndexOf("*/", from, n - from, StringComparison.Ordinal) : -1;
                i = end < 0 ? n : end + 2;
                var isDoc = start + 2 < n && text[start + 2] == '*' && !(start + 3 < n && text[start + 3] == '/');
                tokens.Add(new Token(isDoc ? TokKind.DocComment : TokKind.Comment, text[start..i], start));
                continue;
            }
            if ((c == '/' && i + 1 < n && text[i + 1] == '/') || (c == '#' && !(i + 1 < n && text[i + 1] == '[')))
            {
                while (i < n && text[i] != '\n' && !(text[i] == '?' && i + 1 < n && text[i + 1] == '>')) i++;
                tokens.Add(new Token(TokKind.Comment, text[start..i], start));
                continue;
            }

            // Strings
            if (c == '\'' || c == '"' || c == '`')
            {
                i++;
                while (i < n && text[i] != c)
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                i = Math.Min(n, i + 1);
                tokens.Add(new Token(TokKind.String, text[start..i], start));
                continue;
            }

            // Heredoc / nowdoc
            if (c == '<' && i + 2 < n && text[i + 1] == '<' && text[i + 2] == '<')
            {
                int j = i + 3;
                while (j < n && (text[j] == ' ' || text[j] == '\t')) j++;
                bool quoted = j < n && (text[j] == '\'' || text[j] == '"');
                if (quoted) j++;
                int idStart = j;
                while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                if (j > idStart)
                {
                    var id = text[idStart..j];
                    int lineEnd = text.IndexOf('\n', j);
                    int k = lineEnd < 0 ? n : lineEnd + 1;
                    int stop = n;
                    while (k < n)
                    {
                        int ls = k;
                        while (ls < n && (text[ls] == ' ' || text[ls] == '\t')) ls++;
                        if (string.CompareOrdinal(text, ls, id, 0, id.Length) == 0 &&
                            (ls + id.Length >= n || !(char.IsLetterOrDigit(text[ls + id.Length]) || text[ls + id.Length] == '_')))
                        {
                            stop = ls + id.Length;
                            break;
                        }
                        int nl = text.IndexOf('\n', k);
                        if (nl < 0 || nl >= n) { k = n; break; }
                        k = nl + 1;
                    }
                    i = Math.Min(stop, n);
                    tokens.Add(new Token(TokKind.String, text[start..i], start));
                    continue;
                }
            }

            // Variables
            if (c == '$' && i + 1 < n && (IsIdentStart(text[i + 1]) || text[i + 1] == '$'))
            {
                i++;
                if (text[i] == '$') i++;
                while (i < n && IsIdentPart(text[i])) i++;
                tokens.Add(new Token(TokKind.Variable, text[start..i], start));
                continue;
            }

            // Identifiers (including qualified names)
            if (IsIdentStart(c) || (c == '\\' && i + 1 < n && IsIdentStart(text[i + 1])))
            {
                i++;
                while (i < n && (IsIdentPart(text[i]) || (text[i] == '\\' && i + 1 < n && IsIdentStart(text[i + 1])))) i++;
                // A trailing backslash while typing "App\" is part of the name
                if (i < n && text[i] == '\\') i++;
                tokens.Add(new Token(TokKind.Identifier, text[start..i], start));
                continue;
            }

            // Numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                tokens.Add(new Token(TokKind.Number, text[start..i], start));
                continue;
            }

            // Punctuation
            string? p = null;
            foreach (var s in Puncts3)
                if (i + 3 <= n && string.CompareOrdinal(text, i, s, 0, 3) == 0) { p = s; break; }
            if (p == null)
                foreach (var s in Puncts2)
                    if (i + 2 <= n && string.CompareOrdinal(text, i, s, 0, 2) == 0) { p = s; break; }
            p ??= c.ToString();
            tokens.Add(new Token(TokKind.Punct, p, i));
            i += p.Length;
        }
        return tokens;
    }

    public static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c > 0x7f;
    public static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c > 0x7f;

    /// <summary>Returns the tokens without whitespace (keeps doc comments, which carry type hints).</summary>
    public static List<Token> Significant(List<Token> tokens)
    {
        var list = new List<Token>(tokens.Count);
        foreach (var t in tokens)
            if (t.Kind is not (TokKind.Whitespace or TokKind.Comment or TokKind.InlineHtml or TokKind.OpenTag or TokKind.CloseTag))
                list.Add(t);
        return list;
    }
}
