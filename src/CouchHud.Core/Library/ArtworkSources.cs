namespace CouchHud.Core.Library;

/// <summary>
/// Where artwork may be downloaded from: the hosts of Steam, Epic and SteamGridDB (and their CDNs).
/// Addresses come from catalog files of other programs and from web services, so the host is checked before
/// anything is requested and again for every redirect. Other https addresses (typed in by the user) are not
/// downloaded; the UI loads those directly.
/// </summary>
public static class ArtworkSources
{
    private static readonly string[] Suffixes =
    [
        "steamstatic.com", "steampowered.com", "akamaihd.net",   // Steam and its CDN
        "steamgriddb.com",                                       // cdn2.steamgriddb.com and friends
        "epicgames.com", "unrealengine.com", "akamaized.net"     // Epic's image CDNs
    ];

    public static bool IsAllowedHost(string host) =>
        Suffixes.Any(s => host.Equals(s, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));

    /// <summary>https, default port, no credentials, on an allowed host.</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && IsAllowedHost(uri.IdnHost);

    public static bool IsAllowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowed(uri);

    /// <summary>
    /// The address the UI should use for an artwork URL: the cache host for downloadable sources, null for
    /// everything else (local files, the cache host itself, other websites), which the UI handles as before.
    /// </summary>
    public static string? CacheUrl(string url) =>
        IsAllowed(url) ? ArtworkCache.UrlFor(url) : null;
}
