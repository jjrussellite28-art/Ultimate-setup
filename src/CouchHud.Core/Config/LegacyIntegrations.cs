namespace CouchHud.Core.Config;

// What the core kept for the weather and the news before 0.7 (both are plugins now). Only read, once, for the plugins' first settings.

/// <summary>LEGACY: the weather place as the core stored it.</summary>
public sealed record LegacyPlace(string? Name, string? Region, double Latitude, double Longitude);

/// <summary>LEGACY: a news feed as the core stored it.</summary>
public sealed class LegacyNewsFeed
{
    public string? Name { get; set; }
    public string? Url { get; set; }
}
