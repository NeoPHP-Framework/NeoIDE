using System.IO;
using System.Windows;
using System.Windows.Threading;
using NeoIDE.Editor;
using NeoIDE.Services;

namespace NeoIDE;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        ThemeManager.Apply(AppSettings.Current.Theme);
        SyntaxRegistry.EnsureLoaded();
        DisplayManager.ApplyResources();
        Controls.UpdateToast.Init();
        UpdateService.StartAutomaticChecks();

        var arg = e.Args.FirstOrDefault();
        if (arg != null && Directory.Exists(arg)) OpenProject(arg);
        else ShowWelcome();
    }

    public void ShowWelcome()
    {
        var welcome = new WelcomeWindow();
        MainWindow = welcome;
        if (welcome.ShowDialog() == true && welcome.SelectedProject != null) OpenProject(welcome.SelectedProject);
        else Shutdown();
    }

    public void OpenProject(string path)
    {
        var main = new MainWindow();
        MainWindow = main;
        main.Closed += (_, _) =>
        {
            if (main.ReturnToWelcome) ShowWelcome();
            else Shutdown();
        };
        main.Show();
        main.OpenProject(path);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppSettings.Directory, "errors.log"), $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "NeoIDE — unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
