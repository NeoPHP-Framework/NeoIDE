using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NeoIDE.Dialogs;
using NeoIDE.Services;

namespace NeoIDE.Controls;

/// <summary>
/// "NeoIDE x.y.z is available" notification in the bottom-right corner of the active IDE window.
/// Shown once per version and per session, when the update is downloaded (or available when auto-download is off).
/// </summary>
public sealed class UpdateToast : Window
{
    private static UpdateToast? _current;
    private static string? _shownFor;

    public static void Init() => UpdateService.Changed += OnChanged;

    private static void OnChanged()
    {
        var state = UpdateService.State;
        var version = UpdateService.AvailableVersion;
        var show = state == UpdateState.Ready || (state == UpdateState.Available && !AppSettings.Current.AutoDownloadUpdates);
        if (!show || version == null || _shownFor == version) return;
        if (Application.Current.Windows.OfType<UpdateDialog>().Any()) return;
        var owner = Application.Current.MainWindow;
        if (owner == null || !owner.IsLoaded) return;
        _shownFor = version;
        _current?.Close();
        _current = new UpdateToast(owner, version, state == UpdateState.Ready);
        _current.Show();
    }

    private UpdateToast(Window owner, string version, bool ready)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = owner.FontFamily;
        FontSize = 13;

        var title = new TextBlock { Text = $"NeoIDE {version} is available", FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.Brush("Fg") };
        var detail = new TextBlock
        {
            Text = ready ? "Downloaded — restart to update." : $"Download {UpdateService.FormatSize(UpdateService.DownloadSize)} (changed files only).",
            Foreground = ThemeManager.Brush("FgSecondary"), Margin = new Thickness(0, 3, 0, 0), FontSize = 12,
        };
        var primary = new Button { Content = ready ? "Restart" : "Update", Style = (Style)Application.Current.FindResource("PrimaryButton"), MinWidth = 0, Padding = new Thickness(12, 3, 12, 3) };
        var news = new Button { Content = "What's new", MinWidth = 0, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        var later = new Button { Content = "Later", MinWidth = 0, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        primary.Click += async (_, _) =>
        {
            if (UpdateService.State == UpdateState.Ready) UpdateService.ApplyAndRestart();
            else { Close(); await UpdateService.DownloadAsync(); }
        };
        news.Click += (_, _) => { Close(); UpdateDialog.ShowFor(); };
        later.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(primary);
        buttons.Children.Add(news);
        buttons.Children.Add(later);

        var icon = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
            Foreground = ThemeManager.Brush("Accent"), Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(detail);
        text.Children.Add(buttons);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        Content = new Border
        {
            Background = ThemeManager.Brush("PopupBg"), BorderBrush = ThemeManager.Brush("PopupBorder"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 16, 12), Margin = new Thickness(10), Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
        };

        Loaded += (_, _) => Place();
        owner.LocationChanged += OnOwnerMoved;
        owner.SizeChanged += OnOwnerMoved;
        owner.StateChanged += OnOwnerMoved;
        Closed += (_, _) =>
        {
            owner.LocationChanged -= OnOwnerMoved;
            owner.SizeChanged -= OnOwnerMoved;
            owner.StateChanged -= OnOwnerMoved;
            if (_current == this) _current = null;
        };
    }

    private void OnOwnerMoved(object? sender, EventArgs e) => Place();

    private void Place()
    {
        if (Owner == null) return;
        double left, top, width, height;
        if (Owner.WindowState == WindowState.Maximized)
        {
            var area = SystemParameters.WorkArea;
            (left, top, width, height) = (area.Left, area.Top, area.Width, area.Height);
        }
        else (left, top, width, height) = (Owner.Left, Owner.Top, Owner.ActualWidth, Owner.ActualHeight);
        Left = left + width - ActualWidth - 16;
        Top = top + height - ActualHeight - 36;
    }
}
