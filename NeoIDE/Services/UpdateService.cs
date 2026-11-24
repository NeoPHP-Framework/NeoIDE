using System.IO;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace NeoIDE.Services;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error, NotInstalled }

/// <summary>
/// Updates through Velopack + GitHub Releases of NeoPHP-Framework/NeoIDE.
/// Only the delta packages between the installed version and the new one are downloaded (full package as fallback);
/// the update is applied on restart, or automatically at the next start of the IDE.
/// </summary>
public static class UpdateService
{
    public const string RepositoryUrl = "https://github.com/NeoPHP-Framework/NeoIDE";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private static UpdateManager? _manager;
    private static UpdateInfo? _info;
    private static DispatcherTimer? _timer;
    private static bool _busy;

    public static UpdateState State { get; private set; } = UpdateState.Idle;
    public static int Progress { get; private set; }
    public static string? Error { get; private set; }
    public static string? AvailableVersion => _info?.TargetFullRelease.Version.ToString();
    public static string? ReleaseNotes => _info?.TargetFullRelease.NotesMarkdown;
    /// <summary>True when the download only needs delta packages.</summary>
    public static bool IsDelta => _info?.DeltasToTarget.Length > 0;
    public static long DownloadSize => _info == null ? 0
        : IsDelta ? _info.DeltasToTarget.Sum(d => d.Size) : _info.TargetFullRelease.Size;

    /// <summary>Raised on the UI thread every time the state or the progress changes.</summary>
    public static event Action? Changed;

    public static string CurrentVersion
    {
        get
        {
            try { if (Manager.IsInstalled && Manager.CurrentVersion != null) return Manager.CurrentVersion.ToString(); } catch { }
            return typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>False when NeoIDE runs from Rider / dotnet run / a zip: there is nothing to update.</summary>
    public static bool IsInstalled
    {
        get { try { return Manager.IsInstalled; } catch { return false; } }
    }

    private static UpdateManager Manager => _manager ??= Create();

    private static UpdateManager Create()
    {
        var beta = AppSettings.Current.UpdateChannel == "beta";
        return new UpdateManager(new GithubSource(RepositoryUrl, null, beta));
    }

    /// <summary>Called when the channel changes in the settings.</summary>
    public static void Reset()
    {
        if (State is UpdateState.Downloading or UpdateState.Ready) return;
        _manager = null;
        _info = null;
        Set(UpdateState.Idle);
    }

    /// <summary>Check a few seconds after the start, then every 4 hours (if enabled in the settings).</summary>
    public static void StartAutomaticChecks()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (AppSettings.Current.AutoCheckUpdates) await CheckAsync(userInitiated: false);
        };
        _timer.Start();
    }

    public static async Task CheckAsync(bool userInitiated)
    {
        if (_busy || State is UpdateState.Downloading or UpdateState.Ready) return;
        if (!IsInstalled) { Set(UpdateState.NotInstalled); return; }
        _busy = true;
        Set(UpdateState.Checking);
        try
        {
            _info = await Task.Run(() => Manager.CheckForUpdatesAsync());
            if (_info == null) { Set(UpdateState.UpToDate); return; }
            Set(UpdateState.Available);
            // Background download: the deltas are small, the user is only asked to restart once they are ready.
            if (AppSettings.Current.AutoDownloadUpdates && !userInitiated) await DownloadCoreAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log(ex);
            Set(UpdateState.Error);
        }
        finally { _busy = false; }
    }

    public static async Task DownloadAsync()
    {
        if (_busy || _info == null || State == UpdateState.Ready) return;
        _busy = true;
        try { await DownloadCoreAsync(); }
        finally { _busy = false; }
    }

    private static async Task DownloadCoreAsync()
    {
        if (_info == null) return;
        try
        {
            Progress = 0;
            Set(UpdateState.Downloading);
            var dispatcher = Application.Current.Dispatcher;
            await Task.Run(() => Manager.DownloadUpdatesAsync(_info, p => dispatcher.BeginInvoke(() => { Progress = p; Changed?.Invoke(); })));
            Set(UpdateState.Ready);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log(ex);
            Set(UpdateState.Error);
        }
    }

    /// <summary>Applies the downloaded update and restarts NeoIDE (the current project is reopened).</summary>
    public static void ApplyAndRestart()
    {
        if (_info == null || State != UpdateState.Ready) return;
        var args = Workspace.Root != null ? new[] { Workspace.Root } : Array.Empty<string>();
        Manager.ApplyUpdatesAndRestart(_info.TargetFullRelease, args);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    private static void Set(UpdateState state)
    {
        State = state;
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Changed?.Invoke();
        else d.BeginInvoke(() => Changed?.Invoke());
    }

    private static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppSettings.Directory, "updates.log"), $"[{DateTime.Now:O}] {ex}\n\n"); } catch { }
    }
}
