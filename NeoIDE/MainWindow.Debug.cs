using System.Diagnostics;
using System.IO;
using System.Windows;
using NeoIDE.Controls;
using NeoIDE.Dialogs;
using NeoIDE.Services;
using NeoIDE.Services.Debug;

namespace NeoIDE;

/// <summary>Xdebug debugger: listening, debug run configurations, stepping, breakpoints.</summary>
public partial class MainWindow
{
    private void InitDebugger()
    {
        DebugView.NavigateRequested += (file, line) => OpenDebugLocation(file, line, focus: false);
        DebugView.ListenToggled += () => ListenToggle_Click(this, new RoutedEventArgs());
        DebugService.StateChanged += OnDebugStateChanged;
        DebugService.Paused += OnDebugPaused;
        DebugView.AttachOutput();
        Closed += (_, _) => { DebugService.StateChanged -= OnDebugStateChanged; DebugService.Paused -= OnDebugPaused; DebugView.DetachOutput(); };
        BreakFirstLineItem.IsChecked = AppSettings.Current.BreakAtFirstLine;
        OnDebugStateChanged();
    }

    private void OnDebugStateChanged()
    {
        var s = DebugService.State;
        ListenButton.IsChecked = DebugService.IsListening;
        ListenButton.ToolTip = DebugService.IsListening ? $"Listening for Xdebug on port {DebugService.Port} — click to stop" : "Start listening for PHP debug connections (Xdebug)";
        ListenMenuItem.Header = DebugService.IsListening ? "Stop Listening for PHP Debug Connections" : "Start Listening for PHP Debug Connections";
        MuteMenuItem.IsChecked = Breakpoints.Muted;
        StopButton.IsEnabled = RunConsole.IsRunning || s is DebugState.Paused or DebugState.Running;
        StatusDebugButton.Visibility = s is DebugState.Paused or DebugState.Running ? Visibility.Visible : Visibility.Collapsed;
        StatusDebug.Text = s == DebugState.Paused && DebugService.Position is { } p ? $"Paused {Path.GetFileName(p.file)}:{p.line}" : s == DebugState.Running ? "Debugging" : "";
        // the editors redraw the execution line themselves
    }

    private void OnDebugPaused(string file, int line)
    {
        OpenDebugLocation(file, line, focus: true);
        if (!IsBottomVisible("debug")) ShowBottom("debug");
        if (!IsActive)
        {
            // a breakpoint was hit from the browser: bring the IDE forward
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            TaskbarFlash.Flash(this);
        }
    }

    private void OpenDebugLocation(string file, int line, bool focus)
    {
        if (!File.Exists(file)) { SetStatus($"Paused in {file}:{line} (not a local file — check Run › Debug Settings › Server path)"); return; }
        OpenFile(file, focus: focus);
        if (CurrentEditor is { } ed && line > 0 && line <= ed.Document.LineCount)
        {
            var offset = ed.Document.GetLineByNumber(line).Offset;
            ed.GoToOffset(offset);
            ed.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
        }
    }

    // ------------------------------------------------------------------ listening

    private bool StartListening()
    {
        if (DebugService.IsListening) return true;
        if (DebugService.StartListening(AppSettings.Current.XdebugPort, out var error)) { SetStatus($"Listening for Xdebug on port {DebugService.Port}"); return true; }
        MessageDialog.Show("Debugger", $"Cannot listen on port {AppSettings.Current.XdebugPort}:\n{error}\n\nChange the port in Run › Debug Settings.");
        ListenButton.IsChecked = false;
        return false;
    }

    private void ListenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (DebugService.IsListening) { DebugService.StopListening(); SetStatus("Stopped listening for Xdebug"); }
        else StartListening();
        OnDebugStateChanged();
    }

    /// <summary>The interpreter must load Xdebug: explains how to install it otherwise.</summary>
    private async Task<bool> EnsureXdebugAsync()
    {
        if (!EnsurePhp()) return false;
        var version = await DebugService.XdebugVersion(Workspace.PhpExe!);
        if (version != null) return true;
        var (_, ini, _) = await ProcessRunner.RunAsync(Workspace.PhpExe!, "--ini", null, 10000);
        var loaded = System.Text.RegularExpressions.Regex.Match(ini, @"Loaded Configuration File:\s*(.+)").Groups[1].Value.Trim();
        var extDir = Path.Combine(Path.GetDirectoryName(Workspace.PhpExe!)!, "ext");
        var dll = Directory.Exists(extDir) ? Directory.GetFiles(extDir, "*xdebug*.dll").FirstOrDefault() : null;
        var text = $"Xdebug is not loaded by {Workspace.PhpExe} (PHP {Workspace.PhpVersion}).\n\n" +
                   (dll != null
                       ? $"The extension is installed ({Path.GetFileName(dll)}): enable it in {(loaded.Length > 0 && loaded != "(none)" ? loaded : "php.ini")} with:\n\n    zend_extension=xdebug\n"
                       : "1. Download the DLL matching your PHP on xdebug.org/wizard (paste the output of php -i)\n2. Copy it to " + extDir + "\n3. Add to " + (loaded.Length > 0 && loaded != "(none)" ? loaded : "php.ini") + ":\n\n    zend_extension=xdebug\n") +
                   "\nNo other setting is needed: NeoIDE starts PHP with -dxdebug.mode=debug.\nOpen the Xdebug installation wizard?";
        if (MessageDialog.Confirm("Xdebug", text, "Open xdebug.org/wizard"))
            Process.Start(new ProcessStartInfo("https://xdebug.org/wizard") { UseShellExecute = true });
        return false;
    }

    // ------------------------------------------------------------------ debug run configurations

    private async void DebugServer_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.Root == null || !await EnsureXdebugAsync() || !StartListening()) return;
        if (RunConsole.IsRunning)
        {
            if (!MessageDialog.Confirm("Debug", "The server is already running without the debugger. Restart it with Xdebug?", "Restart")) return;
            RunConsole.Stop();
            await Task.Delay(400);
        }
        SaveAll();
        ShowBottom("debug");
        RunConsole.Clear();
        var port = Workspace.Settings.ServerPort;
        DebugView.Append($"php {NeoScript} serve --port={port} with Xdebug — open http://127.0.0.1:{port} in the browser, the breakpoints stop the requests", false);
        // the built-in server starts a worker per request: they inherit the Xdebug settings of the environment
        await RunConsole.RunAsync(Workspace.PhpExe!, $"{DebugService.PhpArgs(DebugService.Port)} {NeoScript} serve --port={port} --no-ansi", Workspace.Root,
            $"php {NeoScript} serve --port={port} (debug)", DebugService.XdebugEnvironment(DebugService.Port));
    }

    private async void DebugCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.Root == null || !await EnsureXdebugAsync()) return;
        var cmd = InputDialog.Ask("Debug a neo command", "Command and arguments (bin/neo …):", _lastDebugCommand ?? "", "e.g. app:import --dry-run");
        if (string.IsNullOrWhiteSpace(cmd) || !StartListening()) return;
        _lastDebugCommand = cmd.Trim();
        SaveAll();
        ShowBottom("debug");
        await RunConsole.RunAsync(Workspace.PhpExe!, $"{DebugService.PhpArgs(DebugService.Port)} {NeoScript} {_lastDebugCommand} --no-ansi", Workspace.Root, $"php {NeoScript} {_lastDebugCommand} (debug)", DebugService.XdebugEnvironment(DebugService.Port));
    }

    private string? _lastDebugCommand;

    private async void DebugScript_Click(object sender, RoutedEventArgs e)
    {
        var path = CurrentTab?.Path;
        if (path == null || !path.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) { SetStatus("Open a PHP script to debug it"); return; }
        if (!await EnsureXdebugAsync() || !StartListening()) return;
        SaveAll();
        ShowBottom("debug");
        await RunConsole.RunAsync(Workspace.PhpExe!, $"{DebugService.PhpArgs(DebugService.Port)} \"{path}\"", Path.GetDirectoryName(path), $"php {Path.GetFileName(path)} (debug)", DebugService.XdebugEnvironment(DebugService.Port));
    }

    // ------------------------------------------------------------------ stepping, breakpoints

    private void ShowDebug_Click(object sender, RoutedEventArgs e) => ShowBottom(IsBottomVisible("debug") ? null : "debug");
    private void DebugResume_Click(object sender, RoutedEventArgs e) => _ = DebugService.Resume();
    private void DebugStepOver_Click(object sender, RoutedEventArgs e) => _ = DebugService.StepOver();
    private void DebugStepInto_Click(object sender, RoutedEventArgs e) => _ = DebugService.StepInto();
    private void DebugStepOut_Click(object sender, RoutedEventArgs e) => _ = DebugService.StepOut();

    private async void DebugEvaluate_Click(object sender, RoutedEventArgs e)
    {
        if (DebugService.State != DebugState.Paused) { SetStatus("Evaluate: the script is not paused"); return; }
        var initial = CurrentEditor?.SelectedText is { Length: > 0 and < 200 } s ? s : "";
        var expr = InputDialog.Ask("Evaluate expression", "PHP expression (in the selected frame):", initial);
        if (string.IsNullOrWhiteSpace(expr)) return;
        ShowBottom("debug");
        var (value, error) = await DebugService.Evaluate(expr, DebugService.SelectedFrame);
        DebugView.Append(value != null ? $"{expr} = {value.Display}" : $"{expr}: {error}", value == null);
    }

    private void ToggleBreakpoint_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentEditor is { } ed) ed.ToggleBreakpoint(ed.Document.GetLineByOffset(ed.CaretOffset).LineNumber);
    }

    private void MuteBreakpoints_Click(object sender, RoutedEventArgs e) => Breakpoints.SetMuted(MuteMenuItem.IsChecked);

    private void RemoveBreakpoints_Click(object sender, RoutedEventArgs e)
    {
        if (Breakpoints.All.Count == 0) { SetStatus("No breakpoints"); return; }
        if (MessageDialog.Confirm("Breakpoints", $"Remove the {Breakpoints.All.Count} breakpoint(s) of the project?", "Remove")) Breakpoints.RemoveAll();
    }

    private void BreakFirstLine_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.BreakAtFirstLine = BreakFirstLineItem.IsChecked;
        AppSettings.Current.Save();
    }

    private void XdebugPort_Click(object sender, RoutedEventArgs e)
    {
        var v = InputDialog.Ask("Xdebug port", "Port NeoIDE listens on (xdebug.client_port, 9003 by default):", AppSettings.Current.XdebugPort.ToString(), null,
            s => int.TryParse(s, out var n) && n is > 0 and < 65536 ? null : "A port number");
        if (v == null) return;
        AppSettings.Current.XdebugPort = int.Parse(v);
        AppSettings.Current.Save();
        if (DebugService.IsListening) { DebugService.StopListening(); StartListening(); }
    }

    private void ServerPath_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.Root == null) return;
        var v = InputDialog.Ask("Server path", "Path of this project on the server where PHP runs (e.g. /var/www/html in Docker).\nEmpty when PHP runs on this computer:",
            Workspace.Settings.DebugServerRoot ?? "");
        if (v == null) return;
        Workspace.Settings.DebugServerRoot = v.Trim().Length == 0 ? null : v.Trim();
        Workspace.SaveSettings();
    }

    private async void CheckXdebug_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePhp()) return;
        var version = await DebugService.XdebugVersion(Workspace.PhpExe!);
        if (version == null) { await EnsureXdebugAsync(); return; }
        MessageDialog.Show("Xdebug", $"Xdebug {version} is loaded by PHP {Workspace.PhpVersion}.\n\nRun › Debug 'bin/neo serve' (Shift+F9) starts the server with the debugger; " +
            $"or start listening (port {AppSettings.Current.XdebugPort}) and run your own server with xdebug.mode=debug and xdebug.start_with_request=yes.");
    }
}
