using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoIDE.Services;

public sealed class MarketplacePreview
{
    [JsonPropertyName("background")] public string Background { get; set; } = "#1E1F22";
    [JsonPropertyName("foreground")] public string Foreground { get; set; } = "#BCBEC4";
    [JsonPropertyName("keyword")] public string Keyword { get; set; } = "#CF8E6D";
    [JsonPropertyName("string")] public string String { get; set; } = "#6AAB73";
    [JsonPropertyName("function")] public string Function { get; set; } = "#56A8F5";
    [JsonPropertyName("number")] public string Number { get; set; } = "#2AACB8";
    [JsonPropertyName("variable")] public string Variable { get; set; } = "#C77DBB";
    [JsonPropertyName("panel")] public string Panel { get; set; } = "#2B2D30";
    [JsonPropertyName("accent")] public string Accent { get; set; } = "#3574F0";
}

public sealed class MarketplaceItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("dark")] public bool Dark { get; set; } = true;
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("preview")] public MarketplacePreview Preview { get; set; } = new();
}

public sealed class MarketplaceIndex
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("themes")] public List<MarketplaceItem> Themes { get; set; } = new();
    [JsonPropertyName("plugins")] public List<MarketplaceItem> Plugins { get; set; } = new();
}

/// <summary>
/// The NeoIDE marketplace: https://github.com/NeoPHP-Framework/neo-ide-marketplace
/// (index.json + themes/*.json, read from raw.githubusercontent.com — no account or token needed).
/// </summary>
public static class Marketplace
{
    public const string RepositoryUrl = "https://github.com/NeoPHP-Framework/neo-ide-marketplace";
    private const string RawBase = "https://raw.githubusercontent.com/NeoPHP-Framework/neo-ide-marketplace/main/";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NeoIDE");
        return c;
    }

    public static async Task<MarketplaceIndex> FetchIndexAsync()
    {
        // the query string avoids a stale CDN copy right after a push
        var json = await Http.GetStringAsync(RawBase + "index.json?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute);
        return JsonSerializer.Deserialize<MarketplaceIndex>(json) ?? new MarketplaceIndex();
    }

    public static Task<string> DownloadAsync(MarketplaceItem item) =>
        Http.GetStringAsync(RawBase + item.File.TrimStart('/') + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute);

    /// <summary>1.2.10 &gt; 1.2.9 (falls back to string comparison).</summary>
    public static bool IsNewer(string candidate, string installed) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(installed, out var b) ? a > b : string.CompareOrdinal(candidate, installed) > 0;
}
