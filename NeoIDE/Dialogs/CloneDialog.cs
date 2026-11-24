using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NeoIDE.Controls;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>"Get from VCS": git clone with live output, then composer install when the project has a composer.json.</summary>
public sealed class CloneDialog : DialogWindow
{
    private readonly TextBox _url = new();
    private readonly TextBox _dir = new();
    private readonly ConsoleView _log = new() { Height = 220, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _install = new() { Content = "Run composer install after cloning", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private bool _dirEdited;
    public string? ClonedPath { get; private set; }

    public CloneDialog()
    {
        Title = "Get from Version Control";
        var p = new StackPanel { Margin = new Thickness(18, 4, 18, 18), Width = 640 };
        p.Children.Add(Label("Repository URL:", 0));
        p.Children.Add(_url);
        p.Children.Add(Hint("https://github.com/owner/project.git  or  git@github.com:owner/project.git"));
        p.Children.Add(Label("Directory:"));
        var row = new DockPanel();
        var browse = new Button { Content = "Browse…", MinWidth = 0, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_dir);
        p.Children.Add(row);
        p.Children.Add(_install);
        p.Children.Add(_log);
        var clone = Primary("Clone");
        var cancel = Secondary("Cancel");
        p.Children.Add(Buttons(clone, cancel));
        Content = p;

        _dir.Text = AppSettings.Current.DefaultProjectsDirectory;
        _dir.TextChanged += (_, _) => { if (_dir.IsKeyboardFocusWithin) _dirEdited = true; };
        _url.TextChanged += (_, _) =>
        {
            if (_dirEdited) return;
            var m = Regex.Match(_url.Text.Trim(), @"([^/\\:]+?)(\.git)?/?$");
            _dir.Text = Path.Combine(AppSettings.Current.DefaultProjectsDirectory, m.Success ? m.Groups[1].Value : "");
        };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Parent directory" };
            if (dlg.ShowDialog(this) == true) { _dirEdited = true; _dir.Text = Path.Combine(dlg.FolderName, Path.GetFileName(_dir.Text.TrimEnd('\\'))); }
        };
        clone.Click += async (_, _) =>
        {
            var url = _url.Text.Trim();
            var dir = _dir.Text.Trim();
            if (url.Length == 0 || dir.Length == 0) return;
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) { MessageDialog.Show(Title, $"{dir} already exists and is not empty."); return; }
            clone.IsEnabled = false;
            _log.Visibility = Visibility.Visible;
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            var code = await _log.RunAsync("git", $"clone --progress {ProcessRunner.Quote(url)} {ProcessRunner.Quote(dir)}", Path.GetDirectoryName(dir), $"git clone {url}");
            if (code != 0) { clone.IsEnabled = true; return; }
            if (_install.IsChecked == true && File.Exists(Path.Combine(dir, "composer.json")))
            {
                var php = (await PhpLocator.GetDefaultAsync())?.Path;
                var (exe, args) = ComposerService.Command(php, "install --no-interaction");
                await _log.RunAsync(exe, args, dir, "composer install");
            }
            ClonedPath = dir;
            DialogResult = true;
        };
        Loaded += (_, _) => _url.Focus();
    }
}
