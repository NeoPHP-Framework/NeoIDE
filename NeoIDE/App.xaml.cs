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

        // restarted by App.Restart(): wait for the previous instance (its plugin DLLs are locked until it exits)
        var args = e.Args.ToList();
        var wait = args.IndexOf("--wait-pid");
        if (wait >= 0 && wait + 1 < args.Count)
        {
            if (int.TryParse(args[wait + 1], out var pid))
                try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch { }
            args.RemoveRange(wait, 2);
        }

        ThemeManager.Apply(AppSettings.Current.Theme);
        SyntaxRegistry.EnsureLoaded();
        DisplayManager.ApplyResources();
        Controls.UpdateToast.Init();
        UpdateService.StartAutomaticChecks();

        // plugins: staged updates / removals first, then activation (they register their tool windows)
        try { Services.Plugins.PluginManager.ApplyPending(); Services.Plugins.PluginManager.LoadAll(); }
        catch (Exception ex) { Services.Plugins.PluginManager.Log("load: " + ex); }

        var arg = args.FirstOrDefault();
        if (arg != null && Directory.Exists(arg)) OpenProject(arg);
        else ShowWelcome();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.Plugins.PluginManager.DeactivateAll();
        base.OnExit(e);
    }

    /// <summary>Restarts NeoIDE on the current project (plugin changes, settings that need a restart).</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var args = $"--wait-pid {Environment.ProcessId}";
        if (Workspace.Root != null) args += $" \"{Workspace.Root}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false });
        if (Current.MainWindow is MainWindow main) main.Close();
        Current.Shutdown();
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
