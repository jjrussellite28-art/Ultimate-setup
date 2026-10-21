using CouchHud.Core.Config;

namespace CouchHud.Core.Library;

/// <summary>
/// The one rule for web addresses: everything CouchHud shows as a link opens in its own couch browser, and only
/// http(s) goes that way. Other schemes (file:, ms-settings:, steam: ...) are never a "link".
/// Streaming services with copy protection are the one exception: WebView2 has no Widevine, so they stay in Edge.
/// </summary>
public static class WebLinks
{
    private const int MaxLength = 4096;

    /// <summary>An absolute http(s) address without credentials, or false.</summary>
    public static bool TryWebUrl(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host)) return false;
        // https://trusted.example@evil.example/ : the part before the @ is a disguise, not a login.
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    public static bool IsWebUrl(string? text) => TryWebUrl(text, out _);

    // Services whose players need Widevine (or hardware PlayReady) that the embedded browser does not bring along.
    private static readonly string[] DrmHosts =
    [
        "netflix.com", "disneyplus.com", "primevideo.com", "hbomax.com", "max.com", "paramountplus.com", "tv.apple.com",
        "peacocktv.com", "hulu.com", "dazn.com", "wowtv.de", "sky.com", "crunchyroll.com", "magentatv.de", "joyn.de",
        "plus.rtl.de", "tvnow.de", "canalplus.com", "viaplay.com", "discoveryplus.com", "starz.com"
    ];

    public static bool IsDrmService(string? url)
    {
        if (!TryWebUrl(url, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (DrmHosts.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal))) return true;
        // Prime Video also lives under amazon.<country>/gp/video.
        return host.Contains("amazon.", StringComparison.Ordinal) && uri.AbsolutePath.StartsWith("/gp/video", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a new web entry starts with: Edge for known DRM services, the built-in browser for the rest.</summary>
    public static bool DefaultOpenInEdge(string? url) => IsDrmService(url);

    /// <summary>True when this web entry opens in Edge instead of the couch browser.</summary>
    public static bool UsesEdge(CustomEntry entry) =>
        !string.IsNullOrEmpty(entry.Url) && IsWebUrl(entry.Url) && (entry.OpenInEdge ?? DefaultOpenInEdge(entry.Url));

    /// <summary>Entries that never made the choice (before 0.4.0): known DRM domains keep Edge, all others switch to the built-in browser.</summary>
    /// <returns>How many entries were decided.</returns>
    public static int Migrate(IEnumerable<CustomEntry> entries)
    {
        var changed = 0;
        foreach (var e in entries)
        {
            if (e.OpenInEdge is not null || !IsWebUrl(e.Url)) continue;
            e.OpenInEdge = DefaultOpenInEdge(e.Url);
            changed++;
        }
        return changed;
    }
}
