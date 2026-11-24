using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>"New PHP Class" dialog: namespace computed from composer.json PSR-4, parent/interfaces auto-imported.</summary>
public sealed class NewClassDialog : DialogWindow
{
    private readonly TextBox _name = new(), _namespace = new(), _extends = new(), _implements = new();
    private readonly ComboBox _kind = new();
    private readonly CheckBox _final = new() { Content = "final" }, _strict = new() { Content = "declare(strict_types=1)", IsChecked = true };
    private readonly TextBlock _path;
    private readonly string _directory;
    public string? CreatedFile { get; private set; }

    public NewClassDialog(string directory, string? initialName = null, string? kind = null, string? extends = null)
    {
        _directory = directory;
        Title = "New PHP Class";
        _kind.ItemsSource = new[] { "Class", "Interface", "Trait", "Enum", "Abstract class" };
        _kind.SelectedItem = kind ?? "Class";
        _namespace.Text = Workspace.NamespaceForDirectory(directory) ?? "";
        _name.Text = initialName ?? "";
        _extends.Text = extends ?? "";

        var p = new StackPanel { Margin = new Thickness(18, 4, 18, 18), Width = 520 };
        p.Children.Add(Label("Name:", 0));
        p.Children.Add(_name);
        p.Children.Add(Label("Kind:"));
        p.Children.Add(_kind);
        p.Children.Add(Label("Namespace:"));
        p.Children.Add(_namespace);
        p.Children.Add(Hint("Computed from the PSR-4 autoload section of composer.json."));
        p.Children.Add(Label("Extends (class or interface name, auto-imported):"));
        p.Children.Add(_extends);
        p.Children.Add(Label("Implements (comma separated):"));
        p.Children.Add(_implements);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _final.Margin = new Thickness(0, 0, 18, 0);
        row.Children.Add(_final);
        row.Children.Add(_strict);
        p.Children.Add(row);
        _path = Hint("");
        p.Children.Add(_path);
        var ok = Primary("Create");
        ok.Click += (_, _) => Create();
        p.Children.Add(Buttons(ok, Secondary("Cancel")));
        Content = p;

        _name.TextChanged += (_, _) => _path.Text = "File: " + Path.Combine(directory, (_name.Text.Trim().Length > 0 ? _name.Text.Trim() : "…") + ".php");
        _kind.SelectionChanged += (_, _) => _final.IsEnabled = _kind.SelectedItem as string is "Class";
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
        _path.Text = "File: " + directory;
    }

    private void Create()
    {
        var name = _name.Text.Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) { MessageDialog.Show(Title, "Invalid class name."); return; }
        var file = Path.Combine(_directory, name + ".php");
        if (File.Exists(file)) { MessageDialog.Show(Title, $"{file} already exists."); return; }

        var ns = _namespace.Text.Trim().Trim('\\');
        var kind = (string)_kind.SelectedItem;
        var info = new PhpFileInfo { Namespace = ns };
        var uses = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        string Ref(string raw)
        {
            raw = raw.Trim();
            if (raw.Length == 0) return raw;
            string fqn;
            if (raw.Contains('\\')) fqn = raw.TrimStart('\\');
            else
            {
                // look the short name up in the index (prefer project classes)
                var candidates = Workspace.Index.FindByShortName(raw);
                var best = candidates.OrderBy(c => c.File == null ? 2 : c.File.Contains("\\vendor\\") ? 1 : 0).FirstOrDefault();
                fqn = best?.Fqn ?? (ns.Length > 0 ? ns + "\\" + raw : raw);
            }
            var shortName = fqn.Split('\\').Last();
            var fqnNs = fqn.Contains('\\') ? fqn[..fqn.LastIndexOf('\\')] : "";
            if (!string.Equals(fqnNs, ns, StringComparison.OrdinalIgnoreCase) && !(fqnNs.Length == 0 && ns.Length == 0)) uses.Add(fqn);
            return shortName;
        }

        var ext = _extends.Text.Trim().Length > 0 ? Ref(_extends.Text) : null;
        var impl = _implements.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Ref).Where(s => s.Length > 0).ToList();

        var sb = new StringBuilder();
        sb.Append("<?php\n\n");
        if (_strict.IsChecked == true) sb.Append("declare(strict_types=1);\n\n");
        if (ns.Length > 0) sb.Append($"namespace {ns};\n\n");
        foreach (var u in uses) sb.Append($"use {u};\n");
        if (uses.Count > 0) sb.Append('\n');

        var keyword = kind switch
        {
            "Interface" => "interface",
            "Trait" => "trait",
            "Enum" => "enum",
            "Abstract class" => "abstract class",
            _ => _final.IsChecked == true ? "final class" : "class",
        };
        sb.Append($"{keyword} {name}");
        if (ext != null) sb.Append(kind == "Interface" ? $" extends {ext}" : kind is "Class" or "Abstract class" ? $" extends {ext}" : "");
        if (impl.Count > 0 && kind is "Class" or "Abstract class" or "Enum") sb.Append(" implements " + string.Join(", ", impl));
        sb.Append("\n{\n");
        if (kind == "Enum") sb.Append("    case Example;\n");
        else sb.Append("    \n");
        sb.Append("}\n");

        Directory.CreateDirectory(_directory);
        File.WriteAllText(file, sb.ToString());
        CreatedFile = file;
        DialogResult = true;
    }
}
