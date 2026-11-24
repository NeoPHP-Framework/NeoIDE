using System.Windows.Threading;
using NeoIDE.Editor;
using NeoIDE.Services.Plugins;

namespace NeoIDE.Services;

/// <summary>
/// Everything that can be updated: NeoIDE itself (Velopack) and the installed plugins / themes (marketplace).
/// Drives the badges (settings button, Settings pages, taskbar).
/// </summary>
public static class AvailableUpdates
{
    private static MarketplaceIndex? _index;
    private static DispatcherTimer? _timer;

    public static IReadOnlyList<MarketplacePlugin> Plugins { get; private set; } = Array.Empty<MarketplacePlugin>();
    public static IReadOnlyList<MarketplaceItem> Themes { get; private set; } = Array.Empty<MarketplaceItem>();
    public static bool Ide => UpdateService.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready;
    public static int Count => (Ide ? 1 : 0) + Plugins.Count + Themes.Count;

    /// <summary>Raised on the UI thread when the list changes.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        if (_timer != null) return;
        UpdateService.Changed += () => Changed?.Invoke();
        PluginManager.Changed += Recompute;
        ThemeCatalog.Changed += Recompute;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(6);
            if (AppSettings.Current.AutoCheckUpdates) await CheckMarketplaceAsync();
        };
        _timer.Start();
    }

    public static async Task CheckMarketplaceAsync()
    {
        try { SetIndex(await Marketplace.FetchIndexAsync()); }
        catch { /* offline: keep the last result */ }
    }

    /// <summary>Also called by the Settings dialog with the index it just fetched.</summary>
    public static void SetIndex(MarketplaceIndex index)
    {
        _index = index;
        Recompute();
    }

    private static void Recompute()
    {
        if (_index == null) return;
        Plugins = _index.Plugins.Where(m => PluginManager.Find(m.Id) is { } p
                                            && Marketplace.IsNewer(m.Version, p.PendingVersion ?? p.Manifest.Version) && !p.PendingRemoval).ToList();
        Themes = _index.Themes.Where(m => ThemeCatalog.Find(m.Id) is { IsBundled: false } t && Marketplace.IsNewer(m.Version, t.Version)).ToList();
        Changed?.Invoke();
    }

    public static string Describe()
    {
        var parts = new List<string>();
        if (Ide) parts.Add($"NeoIDE {UpdateService.AvailableVersion}");
        if (Plugins.Count > 0) parts.Add($"{Plugins.Count} plugin update(s)");
        if (Themes.Count > 0) parts.Add($"{Themes.Count} theme update(s)");
        return parts.Count == 0 ? "Everything is up to date" : string.Join(" · ", parts) + " available";
    }
}
