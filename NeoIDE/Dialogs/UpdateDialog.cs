using System.Windows;
using System.Windows.Controls;
using NeoIDE.Controls;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>Help › Check for updates: state, release notes, download progress, restart.</summary>
public sealed class UpdateDialog : DialogWindow
{
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 12, 0, 0), Maximum = 100 };
    private readonly MarkdownView _notes = new() { Height = 260, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _notesBorder;
    private readonly Button _main = Primary("Check again");
    private readonly Button _close = Secondary("Close");

    private UpdateDialog()
    {
        Title = "NeoIDE updates";
        _title.Foreground = ThemeManager.Brush("Fg");
        _detail.Foreground = ThemeManager.Brush("FgSecondary");
        _notesBorder = new Border
        {
            BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 0),
            Child = _notes, Visibility = Visibility.Collapsed,
        };
        _notes.Margin = new Thickness(0);

        var panel = new StackPanel { Margin = new Thickness(20, 6, 20, 18), Width = 560 };
        panel.Children.Add(_title);
        panel.Children.Add(_detail);
        panel.Children.Add(_progress);
        panel.Children.Add(_notesBorder);
        panel.Children.Add(Buttons(_main, _close));
        Content = panel;

        _main.Click += async (_, _) =>
        {
            switch (UpdateService.State)
            {
                case UpdateState.Available: await UpdateService.DownloadAsync(); break;
                case UpdateState.Ready: UpdateService.ApplyAndRestart(); break;
                case UpdateState.NotInstalled: System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateService.RepositoryUrl + "/releases") { UseShellExecute = true }); break;
                default: await UpdateService.CheckAsync(userInitiated: true); break;
            }
        };
        UpdateService.Changed += Refresh;
        Closed += (_, _) => UpdateService.Changed -= Refresh;
        Refresh();
        Loaded += async (_, _) =>
        {
            if (UpdateService.State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Error)
                await UpdateService.CheckAsync(userInitiated: true);
        };
    }

    public static void ShowFor() => new UpdateDialog().ShowDialog();

    private string _notesShown = "";

    private void Refresh()
    {
        var current = UpdateService.CurrentVersion;
        var next = UpdateService.AvailableVersion;
        _progress.Visibility = Visibility.Collapsed;
        _main.IsEnabled = true;
        _main.Visibility = Visibility.Visible;
        switch (UpdateService.State)
        {
            case UpdateState.Checking:
                _title.Text = "Checking for updates…";
                _detail.Text = $"Installed version: {current}";
                _progress.Visibility = Visibility.Visible;
                _progress.IsIndeterminate = true;
                _main.IsEnabled = false;
                break;
            case UpdateState.UpToDate or UpdateState.Idle:
                _title.Text = "NeoIDE is up to date";
                _detail.Text = $"Version {current} — channel {AppSettings.Current.UpdateChannel}.";
                _main.Content = "Check again";
                break;
            case UpdateState.Available:
                _title.Text = $"NeoIDE {next} is available";
                _detail.Text = $"Installed: {current}. Download: {Size()} — only the changed files are downloaded.";
                _main.Content = "Download";
                break;
            case UpdateState.Downloading:
                _title.Text = $"Downloading NeoIDE {next}…";
                _detail.Text = $"{UpdateService.Progress}% of {Size()}";
                _progress.Visibility = Visibility.Visible;
                _progress.IsIndeterminate = false;
                _progress.Value = UpdateService.Progress;
                _main.IsEnabled = false;
                _main.Content = "Download";
                break;
            case UpdateState.Ready:
                _title.Text = $"NeoIDE {next} is ready";
                _detail.Text = "Restart to finish the update (your project is reopened). Otherwise it is applied the next time you start NeoIDE.";
                _main.Content = "Restart now";
                break;
            case UpdateState.NotInstalled:
                _title.Text = "Updates are not available for this copy";
                _detail.Text = "NeoIDE runs from a build folder (Rider, dotnet run or zip). Install it with NeoIDE-win-Setup.exe from the GitHub releases to receive the automatic updates.";
                _main.Content = "Open releases";
                break;
            case UpdateState.Error:
                _title.Text = "Could not check for updates";
                _detail.Text = UpdateService.Error ?? "Unknown error.";
                _main.Content = "Retry";
                break;
        }

        var notes = UpdateService.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready ? UpdateService.ReleaseNotes : null;
        _notesBorder.Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(notes) && notes != _notesShown)
        {
            _notesShown = notes;
            _notes.Render(notes);
        }
    }

    private static string Size() => UpdateService.FormatSize(UpdateService.DownloadSize);
}
