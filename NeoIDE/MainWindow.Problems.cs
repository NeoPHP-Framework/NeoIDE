using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NeoIDE.Dialogs;
using NeoIDE.Editor;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;

namespace NeoIDE;

/// <summary>Problems tool window, PHPStan on save, code analysis settings.</summary>
public partial class MainWindow
{
    private void InitProblems()
    {
        ProblemsView.CurrentFile = () => CurrentTab?.Editor?.FilePath;
        ProblemsView.NavigateRequested += (file, line, offset) =>
        {
            if (offset >= 0) OpenFile(file, offset);
            else OpenFileAt(file, line);
        };
        ProblemsView.StatusMessage += SetStatus;
        ProblemsView.InstallPhpStanRequested += async () => await RunComposer("require --dev phpstan/phpstan");
        DiagnosticsStore.Changed += file => Dispatcher.BeginInvoke(() =>
        {
            if (file == null || CurrentTab?.Path is { } p && string.Equals(Path.GetFullPath(p), file, StringComparison.OrdinalIgnoreCase)) RefreshProblemsStatus();
        });
        NeoGutterMargin.Ide = (file, offset) => OpenFile(file, offset);
        LintItem.IsChecked = AppSettings.Current.LintWhileTyping;
        PhpStanOnSaveItem.IsChecked = AppSettings.Current.PhpStanOnSave;
        NeoChecksItem.IsChecked = AppSettings.Current.NeoChecks;
        FormatOnSaveItem.IsChecked = AppSettings.Current.FormatOnSave;
        ShowUsagesItem.IsChecked = AppSettings.Current.ShowUsages;
        RefreshProblemsStatus();
    }

    /// <summary>Status bar: errors / warnings of the current file (a green check when there is none).</summary>
    private void RefreshProblemsStatus()
    {
        StatusProblems.Children.Clear();
        var path = CurrentTab?.Editor?.FilePath;
        if (path == null) { StatusProblemsButton.Visibility = Visibility.Collapsed; return; }
        StatusProblemsButton.Visibility = Visibility.Visible;
        var (errors, warnings) = DiagnosticsStore.Count(path);
        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        void Add(string glyph, string text, string color)
        {
            StatusProblems.Children.Add(new TextBlock { Text = glyph, FontFamily = icons, FontSize = 11, Margin = new Thickness(0, 1, 4, 0), Foreground = ThemeManager.Brush(color), VerticalAlignment = VerticalAlignment.Center });
            if (text.Length > 0) StatusProblems.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 8, 0), Foreground = ThemeManager.Brush(color), VerticalAlignment = VerticalAlignment.Center });
        }
        if (errors == 0 && warnings == 0) Add("", "", "SuccessFg");
        if (errors > 0) Add("", errors.ToString(), "ErrorFg");
        if (warnings > 0) Add("", warnings.ToString(), "WarnFg");
        StatusProblemsButton.ToolTip = errors + warnings == 0 ? "No problems in this file (Alt+6: Problems)" : $"{errors} error(s), {warnings} warning(s) in this file — F2: next problem, Alt+6: Problems";
    }

    // ------------------------------------------------------------------ PHPStan on save

    private readonly Dictionary<string, CancellationTokenSource> _phpStanRuns = new(StringComparer.OrdinalIgnoreCase);

    private async void PhpStanOnSave(string path)
    {
        if (!AppSettings.Current.PhpStanOnSave || Workspace.Root == null || Workspace.PhpExe == null) return;
        if (Checkers.PhpStanBinary(Workspace.Root) == null) return;
        if (!path.StartsWith(Workspace.Root, StringComparison.OrdinalIgnoreCase) || path.Contains("\\vendor\\")) return;
        if (_phpStanRuns.Remove(path, out var previous)) previous.Cancel();
        var cts = new CancellationTokenSource();
        _phpStanRuns[path] = cts;
        try
        {
            await Task.Delay(500, cts.Token);
            var (list, _) = await Checkers.PhpStanAsync(new[] { path }, 120000);
            if (cts.IsCancellationRequested) return;
            DiagnosticsStore.Set(path, "phpstan", list.Where(d => string.Equals(Path.GetFullPath(d.File), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).ToList());
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_phpStanRuns.TryGetValue(path, out var c) && c == cts) _phpStanRuns.Remove(path);
        }
    }

    // ------------------------------------------------------------------ menu

    private void ShowProblems_Click(object sender, RoutedEventArgs e) => ShowBottom(IsBottomVisible("problems") ? null : "problems");
    private void NextProblem_Click(object sender, RoutedEventArgs e) => CurrentEditor?.GoToProblem(1);
    private void PreviousProblem_Click(object sender, RoutedEventArgs e) => CurrentEditor?.GoToProblem(-1);

    private async void InspectCode_Click(object sender, RoutedEventArgs e)
    {
        SaveAll();
        ShowBottom("problems");
        await ProblemsView.InspectProjectAsync();
    }

    private void LintToggle_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.LintWhileTyping = LintItem.IsChecked;
        AppSettings.Current.Save();
        if (!LintItem.IsChecked) foreach (var t in _tabs) if (t.Editor?.FilePath is { } f) DiagnosticsStore.Set(f, "php", Array.Empty<Diagnostic>());
    }

    private void ShowUsages_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.ShowUsages = ShowUsagesItem.IsChecked;
        AppSettings.Current.Save();
        foreach (var t in _tabs) if (t.Editor != null) { if (ShowUsagesItem.IsChecked) t.Editor.RefreshUsages(); else t.Editor.ClearUsages(); }
    }

    private void FormatOnSave_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.FormatOnSave = FormatOnSaveItem.IsChecked;
        AppSettings.Current.Save();
    }

    private async void InstallCsFixer_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.Root == null) { SetStatus("Open a project first"); return; }
        if (CodeFormatter.PhpCsFixerBinary(Workspace.Root) != null) { SetStatus("PHP-CS-Fixer is already installed: Ctrl+Alt+L reformats the PHP files with it"); return; }
        await RunComposer("require --dev friendsofphp/php-cs-fixer");
    }

    /// <summary>Ctrl+S: saves everything; with "Reformat Code on Save", the modified files are reformatted first.</summary>
    private async void SaveAllFromKeyboard()
    {
        if (AppSettings.Current.FormatOnSave)
            foreach (var t in _tabs.Where(t => t.Dirty && t.Editor != null).ToList())
            {
                try { await t.Editor!.ReformatAsync(); } catch { }
            }
        SaveAll();
        SetStatus(AppSettings.Current.FormatOnSave ? "All files reformatted and saved" : "All files saved");
    }

    private void PhpStanOnSave_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.PhpStanOnSave = PhpStanOnSaveItem.IsChecked;
        AppSettings.Current.Save();
    }

    private void NeoChecks_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.NeoChecks = NeoChecksItem.IsChecked;
        AppSettings.Current.Save();
        Php.NeoProject.Invalidate();
    }

    private void PhpStanLevel_Click(object sender, RoutedEventArgs e)
    {
        var v = InputDialog.Ask("PHPStan level", "Level used when the project has no phpstan.neon (0 = basic checks … 10 = strictest):",
            AppSettings.Current.PhpStanLevel.ToString(), null, s => int.TryParse(s, out var n) && n is >= 0 and <= 10 ? null : "A number between 0 and 10");
        if (v == null) return;
        AppSettings.Current.PhpStanLevel = int.Parse(v);
        AppSettings.Current.Save();
    }
}
