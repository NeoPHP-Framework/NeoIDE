using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>Template variables (PHP views, Twig), the NeoPHP quick fixes and Reformat Code.</summary>
public sealed partial class CodeEditor
{
    // ------------------------------------------------------------------ template variables

    /// <summary>A variable / attribute of a template at the offset (Twig, or a $variable of a PHP view).</summary>
    public TwigSymbol? TemplateSymbolAt(int offset)
    {
        if (IsPreview || FilePath == null || offset < 0 || offset > Document.TextLength) return null;
        if (Language == "Twig") return TwigInsight.SymbolAt(Text, offset, FilePath);
        if (!IsPhp) return null;
        var text = Text;
        if (TemplateVariables.PhpViewVariableAt(text, offset, FilePath) is not { } v) return null;
        int s = offset;
        while (s > 0 && PhpLexer.IsIdentPart(text[s - 1])) s--;
        return new TwigSymbol(s - 1, v.Name.Length + 1, v.File, v.Offset, TemplateVariables.Describe(v));
    }

    // ------------------------------------------------------------------ quick fixes

    /// <summary>NeoPHP and Twig fixes of the problem at the caret, put first in the Alt+Enter menu.</summary>
    private void AddTemplateQuickFixes(ContextMenu menu, int caret)
    {
        foreach (var (header, action) in Enumerable.Reverse(TemplateQuickFixes(caret)))
        {
            var mi = new MenuItem { Header = header.Literal() };
            mi.Click += (_, _) => action();
            menu.Items.Insert(0, mi);
        }
    }

    /// <summary>The NeoPHP / Twig fixes available at the caret (label, action).</summary>
    public List<(string label, Action action)> TemplateQuickFixes(int caret)
    {
        var list = new List<(string, Action)>(UndefinedQuickFixes(caret));
        if (NeoRefAt(caret) is { } r && NeoInsight.Problem(r) != null)
        {
            foreach (var name in NeoInsight.Suggestions(r))
            {
                var n = name;
                list.Add(($"Change to '{n}'", () => Replace(r.Start, r.Length, n)));
            }
            switch (r.Kind)
            {
                case NeoRefKind.Template when !r.Value.StartsWith("@"):
                    list.Add(($"Create template '{r.Value}'", () => CreateTemplate(r.Value)));
                    break;
                case NeoRefKind.Env:
                    list.Add(($"Add '{r.Value}' to .env", () => AddEnvVariable(r.Value)));
                    break;
                case NeoRefKind.Parameter:
                    list.Add(($"Add parameter '{r.Value}' to config/services.yaml", () => AddParameter(r.Value)));
                    break;
            }
        }
        if (Language == "Twig" && FilePath != null && !IsPreview)
        {
            var problem = TwigInsight.Problems(Text, FilePath).FirstOrDefault(p => caret >= p.Start && caret <= p.Start + p.Length);
            if (problem != null)
                foreach (var name in problem.Suggestions)
                {
                    var n = name;
                    list.Add(($"Change to '{n}'", () => Replace(problem.Start, problem.Length, n)));
                }
        }
        return list;
    }

    private void Replace(int start, int length, string text)
    {
        if (start < 0 || start + length > Document.TextLength) return;
        Document.Replace(start, length, text);
        CaretOffset = start + text.Length;
        Analyze();
    }

    /// <summary>The file a fix created / changed: the NeoPHP model is refreshed and the file opened.</summary>
    private void Opened(string file, int offset)
    {
        TemplateVariables.Invalidate();
        NeoProject.Invalidate(file);
        NavigateRequested?.Invoke(file, offset);
    }

    /// <summary>Creates templates/&lt;name&gt; extending the base layout (PHP view or Twig, like the project).</summary>
    public string? CreateTemplate(string name)
    {
        var root = Workspace.Root;
        if (root == null) return null;
        name = name.Trim().Replace('\\', '/').TrimStart('/');
        var hasExt = name.EndsWith(".php", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".twig", StringComparison.OrdinalIgnoreCase);
        var names = NeoProject.TemplateNames;
        var twig = hasExt ? name.EndsWith(".twig", StringComparison.OrdinalIgnoreCase)
            : Language == "Twig" || names.Any(t => t.EndsWith(".twig", StringComparison.OrdinalIgnoreCase)) && !names.Any(t => t.EndsWith(".php", StringComparison.OrdinalIgnoreCase));
        var file = Path.GetFullPath(Path.Combine(root, "templates", hasExt ? name : name + (twig ? ".html.twig" : ".php")));
        if (File.Exists(file)) { Opened(file, 0); return file; }
        string content;
        int caret;
        if (twig)
        {
            var baseFile = NeoProject.Template("base.html.twig");
            var block = BlockName(baseFile, @"\{%-?\s*block\s+(\w+)", "body");
            content = baseFile != null ? $"{{% extends 'base.html.twig' %}}\n\n{{% block {block} %}}\n    \n{{% endblock %}}\n" : "\n";
            caret = baseFile != null ? content.IndexOf("    \n", StringComparison.Ordinal) + 4 : 0;
        }
        else
        {
            var baseFile = NeoProject.Template("base");
            var isPhpBase = baseFile != null && baseFile.EndsWith(".php", StringComparison.OrdinalIgnoreCase);
            var section = BlockName(baseFile, @"->section\(\s*['""](\w+)", "content");
            content = isPhpBase ? $"<?php $this->extend('base') ?>\n\n<?php $this->start('{section}') ?>\n\n<?php $this->stop() ?>\n" : "\n";
            caret = isPhpBase ? content.IndexOf("?>\n\n<?php $this->stop", StringComparison.Ordinal) + 3 : 0;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        StatusMessage?.Invoke($"Created templates/{Path.GetRelativePath(Path.Combine(root, "templates"), file).Replace('\\', '/')}");
        Opened(file, Math.Max(0, caret));
        return file;
    }

    private static string BlockName(string? file, string pattern, string preferred)
    {
        if (file == null || !File.Exists(file)) return preferred;
        var names = Regex.Matches(File.ReadAllText(file), pattern).Select(m => m.Groups[1].Value).ToList();
        return names.Contains(preferred) ? preferred : names.FirstOrDefault(n => n is "body" or "content" or "main") ?? names.FirstOrDefault() ?? preferred;
    }

    /// <summary>Appends NAME= to the .env of the project and opens it on the value.</summary>
    public string? AddEnvVariable(string name)
    {
        var root = Workspace.Root;
        if (root == null) return null;
        var file = Path.Combine(root, ".env");
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var prefix = text.Length > 0 && !text.EndsWith("\n") ? nl : "";
        var line = $"{name}=";
        File.WriteAllText(file, text + prefix + line + nl);
        StatusMessage?.Invoke($"Added {name} to .env (real secrets belong in .env.local)");
        Opened(file, text.Length + prefix.Length + line.Length);
        return file;
    }

    /// <summary>Adds the parameter to the "parameters:" section of config/services.yaml (created when missing).</summary>
    public string? AddParameter(string name)
    {
        var root = Workspace.Root;
        if (root == null) return null;
        var file = Path.Combine(root, "config", "services.yaml");
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var section = Regex.Match(text, @"^parameters:[ \t]*(?:\{\s*\})?[ \t]*\r?\n", RegexOptions.Multiline);
        string updated;
        int caret;
        if (section.Success)
        {
            var rest = text[(section.Index + section.Length)..];
            var next = Regex.Match(rest, @"^([ \t]+)\S");
            var indent = next.Success ? next.Groups[1].Value : "    ";
            var entry = $"{indent}{name}: ''";
            var header = Regex.Replace(section.Value, @"\{\s*\}", "").TrimEnd() + nl;
            updated = text[..section.Index] + header + entry + nl + rest;
            caret = section.Index + header.Length + entry.Length - 1;
        }
        else
        {
            var entry = $"parameters:{nl}    {name}: ''";
            updated = entry + nl + (text.Length > 0 ? nl : "") + text;
            caret = entry.Length - 1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, updated);
        StatusMessage?.Invoke($"Added parameter {name} to config/services.yaml");
        Opened(file, caret);
        return file;
    }

    // ------------------------------------------------------------------ reformat

    public bool IsReformatting { get; private set; }

    /// <summary>
    /// Code › Reformat Code (Ctrl+Alt+L): PHP-CS-Fixer for PHP classes when installed (the project's
    /// .php-cs-fixer(.dist).php, else PER-CS), JSON pretty print, otherwise the built-in re-indentation.
    /// With a selection, only the selected lines are re-indented. The change is one undo step.
    /// </summary>
    public async Task<bool> ReformatAsync()
    {
        if (IsReadOnly || IsPreview || IsReformatting) return false;
        var unit = Options.ConvertTabsToSpaces ? new string(' ', Options.IndentationSize) : "\t";
        var text = Text;
        if (!TextArea.Selection.IsEmpty)
        {
            var a = Document.GetLineByOffset(SelectionStart).LineNumber;
            var b = Document.GetLineByOffset(Math.Max(SelectionStart, SelectionStart + SelectionLength - 1)).LineNumber;
            var changed = ApplyFormatted(text, Reindenter.Reformat(text, Language, unit, a, b));
            StatusMessage?.Invoke(changed ? $"Reformatted lines {a}–{b}" : "Already formatted");
            return changed;
        }
        string formatted;
        string how;
        var root = Workspace.Root;
        if (Language == "JSON")
        {
            var json = CodeFormatter.FormatJson(text, Options.IndentationSize);
            if (json == null) { StatusMessage?.Invoke("Not valid JSON: fix the syntax errors first"); return false; }
            formatted = json;
            how = "JSON";
        }
        else if (IsPhp && FilePath != null && root != null && Workspace.PhpExe != null && CodeFormatter.PhpCsFixerBinary(root) != null && !TemplateVariables.IsPhpView(FilePath))
        {
            IsReformatting = true;
            StatusMessage?.Invoke("Reformatting with PHP-CS-Fixer…");
            try
            {
                var (result, error) = await CodeFormatter.PhpCsFixerAsync(FilePath, text);
                if (result == null) { StatusMessage?.Invoke("PHP-CS-Fixer: " + error); return false; }
                if (Text != text) { StatusMessage?.Invoke("The file changed during the reformat: run it again"); return false; }
                formatted = result;
                how = CodeFormatter.PhpCsFixerConfig(root) is { } cfg ? $"PHP-CS-Fixer ({Path.GetFileName(cfg)})" : $"PHP-CS-Fixer ({CodeFormatter.DefaultRules})";
            }
            finally { IsReformatting = false; }
        }
        else
        {
            formatted = Reindenter.Reformat(text, Language, unit);
            how = Reindenter.CanReindent(Language) ? "indentation" : "whitespace";
            if (IsPhp && !TemplateVariables.IsPhpView(FilePath) && root != null && CodeFormatter.PhpCsFixerBinary(root) == null)
                how += " only — Code › Install PHP-CS-Fixer for the complete PHP style";
        }
        var done = ApplyFormatted(text, formatted);
        StatusMessage?.Invoke(done ? $"Reformatted: {how}" : $"Already formatted ({how})");
        return done;
    }

    /// <summary>Replaces only the part that changed, and keeps the caret at its place in the code of its line.</summary>
    private bool ApplyFormatted(string old, string formatted)
    {
        if (old == formatted) return false;
        int prefix = 0, max = Math.Min(old.Length, formatted.Length);
        while (prefix < max && old[prefix] == formatted[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && old[old.Length - 1 - suffix] == formatted[formatted.Length - 1 - suffix]) suffix++;
        var caretLine = Document.GetLineByOffset(CaretOffset);
        var lineNo = caretLine.LineNumber;
        var lineText = Document.GetText(caretLine);
        var indentBefore = lineText.Length - lineText.TrimStart().Length;
        var inCode = Math.Max(0, CaretOffset - caretLine.Offset - indentBefore);
        Document.Replace(prefix, old.Length - prefix - suffix, formatted.Substring(prefix, formatted.Length - prefix - suffix));
        var line = Document.GetLineByNumber(Math.Min(lineNo, Document.LineCount));
        var newText = Document.GetText(line);
        var indentAfter = newText.Length - newText.TrimStart().Length;
        CaretOffset = line.Offset + Math.Min(newText.Length, indentAfter + inCode);
        Analyze();
        return true;
    }
}
