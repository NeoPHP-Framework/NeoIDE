using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NeoIDE.Dialogs;
using NeoIDE.Services;

namespace NeoIDE;

public sealed record RecentItem(string Name, string Path)
{
    private static readonly string[] Palette = { "#3574F0", "#5FB865", "#E08855", "#B189F5", "#C75450", "#21A0B2", "#D5A72C" };

    public string Initials
    {
        get
        {
            var parts = Name.Split(new[] { '-', '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var s = parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : Name.Length >= 2 ? Name[..2] : Name;
            return s.ToUpperInvariant();
        }
    }

    public Brush Color => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[Math.Abs(Name.GetHashCode()) % Palette.Length]));
}

public partial class WelcomeWindow : Window
{
    public string? SelectedProject { get; private set; }

    public WelcomeWindow()
    {
        InitializeComponent();
        DisplayManager.Attach(this, 36);
        WindowPlacement.Attach(this);
        VersionText.Text = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        ThemeCombo.ItemsSource = Editor.ThemeCatalog.All.Select(t => t.Name).ToList();
        ThemeCombo.SelectedItem = ThemeManager.CurrentName;
        FontCombo.ItemsSource = new[] { 12.0, 12.5, 13.0, 13.5, 14.0, 15.0, 16.0, 18.0 };
        FontCombo.SelectedItem = AppSettings.Current.EditorFontSize;
        LoadRecent();
        Loaded += async (_, _) => PhpList.ItemsSource = await PhpLocator.FindAllAsync();
    }

    private void LoadRecent(string filter = "")
    {
        var items = AppSettings.Current.RecentProjects
            .Where(r => Directory.Exists(r.Path))
            .Select(r => new RecentItem(r.Name, r.Path))
            .Where(r => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        RecentList.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 && filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = EmptyState.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => LoadRecent(SearchBox.Text.Trim());

    private void Nav_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsPage == null) return;
        ProjectsPage.Visibility = NavList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        CustomizePage.Visibility = NavList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ThemeCombo.SelectedItem is string name && name != ThemeManager.CurrentName) ThemeManager.Apply(name);
        Editor.SyntaxRegistry.ApplyTheme();
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FontCombo.SelectedItem is double size)
        {
            AppSettings.Current.EditorFontSize = size;
            AppSettings.Current.Save();
        }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewProjectWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (dlg.ShowDialog() == true && dlg.CreatedPath != null) Choose(dlg.CreatedPath);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Open project directory" };
        if (dlg.ShowDialog(this) == true) Choose(dlg.FolderName);
    }

    private void Clone_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CloneDialog { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (dlg.ShowDialog() == true && dlg.ClonedPath != null) Choose(dlg.ClonedPath);
    }

    private void Choose(string path)
    {
        SelectedProject = path;
        DialogResult = true;
    }

    private void Recent_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem r) Choose(r.Path);
    }

    private void Recent_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RecentList.SelectedItem is RecentItem r) Choose(r.Path);
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem r) Choose(r.Path);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem r) Process.Start("explorer.exe", r.Path);
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem r) return;
        AppSettings.Current.RecentProjects.RemoveAll(p => string.Equals(p.Path, r.Path, StringComparison.OrdinalIgnoreCase));
        AppSettings.Current.Save();
        LoadRecent(SearchBox.Text.Trim());
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
