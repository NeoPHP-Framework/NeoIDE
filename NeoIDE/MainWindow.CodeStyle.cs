using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Services;

namespace NeoIDE;

/// <summary>Status bar: indentation of the current file (click: change it for this file, or the settings).</summary>
public partial class MainWindow
{
    private void UpdateIndentStatus()
    {
        var ed = CurrentEditor;
        StatusIndent.Text = ed == null ? "" : ed.Indent.Label;
        StatusIndent.ToolTip = ed == null ? null : ed.Indent.Source switch
        {
            ".editorconfig" => "Indentation of the .editorconfig of the project — click to change",
            "detected" => "Indentation detected in the file — click to change",
            "manual" => "Indentation chosen for this file — click to change",
            "general" => "General indentation (Settings › Code Style) — click to change",
            var lang => $"Indentation of {lang} files (Settings › Code Style) — click to change",
        };
    }

    private void StatusIndent_Click(object sender, MouseButtonEventArgs e)
    {
        var ed = CurrentEditor;
        if (ed == null) return;
        var menu = new ContextMenu { PlacementTarget = StatusIndent, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Add(string header, Action action, bool check = false)
        {
            var mi = new MenuItem { Header = header, IsCheckable = check, IsChecked = check };
            mi.Click += (_, _) => { action(); UpdateIndentStatus(); };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new MenuItem { Header = "Indentation of this file", IsEnabled = false });
        foreach (var (size, tabs) in new[] { (2, false), (4, false), (8, false), (4, true) })
        {
            var r = new IndentResolved(size, tabs, "manual");
            Add(r.Label, () => ed.SetIndent(r), ed.Indent.Size == size && ed.Indent.UseTabs == tabs);
        }
        if (ed.Indent.Source == "manual") Add("Use the Code Style settings", () => ed.SetIndent(null));
        menu.Items.Add(new Separator());
        Add("Reformat the file with this indentation", () => _ = ed.ReformatAsync());
        Add("Configure Code Style…", () => OpenSettings("Code Style"));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenSettings(string page)
    {
        new Dialogs.SettingsDialog(page).ShowDialog();
    }
}
