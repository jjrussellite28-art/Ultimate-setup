using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CouchHud.Core.Integrations;

public sealed class NewsFeed
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed record NewsItem(
    string Id,
    string Feed,
    string Title,
    string Summary,
    string Link,
    string? Image,
    long Published,
    IReadOnlyList<string> Paragraphs);

/// <summary>
/// RSS 2.0 and Atom feeds for the News tab. Parsed with plain LINQ to XML, so
/// odd feeds degrade to "fewer fields" instead of failing.
/// </summary>
public sealed partial class NewsService(HttpClient http)
{
    public static IReadOnlyList<NewsFeed> Defaults { get; } =
    [
        new() { Name = "WinFuture", Url = "https://static.winfuture.de/feeds/WinFuture-News-rss2.0.xml" },
        new() { Name = "Caschys Blog", Url = "https://stadt-bremerhaven.de/feed/" }
    ];

    /// <summary>Default feeds for English: the German ones are not much use to someone who reads English.</summary>
    public static IReadOnlyList<NewsFeed> DefaultsEnglish { get; } =
    [
        new() { Name = "The Verge Games", Url = "https://www.theverge.com/rss/games/index.xml" },
        new() { Name = "PC Gamer", Url = "https://www.pcgamer.com/rss/" }
    ];

    /// <summary>
    /// The feeds to show. As long as the list is still one of the two default sets (the user changed nothing),
    /// it follows the language; once a feed was added or removed it is the user's own list.
    /// </summary>
    public static IReadOnlyList<NewsFeed> Effective(IReadOnlyList<NewsFeed> configured, string language)
    {
        var urls = configured.Select(f => f.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Same(IReadOnlyList<NewsFeed> set) => urls.SetEquals(set.Select(f => f.Url));
        if (!Same(Defaults) && !Same(DefaultsEnglish)) return configured;
        return language == "en" ? DefaultsEnglish : Defaults;
    }

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<NewsItem> Items)> _cache = new();

    /// <summary>All feeds merged, newest first. A broken feed is skipped, not fatal.</summary>
    public async Task<IReadOnlyList<NewsItem>> GetAsync(IEnumerable<NewsFeed> feeds, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(feeds.Select(async Task<IReadOnlyList<NewsItem>> (NewsFeed f) =>
        {
            try
            {
                return await GetFeedAsync(f, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException)
            {
                Console.Error.WriteLine($"[news] {f.Name}: {ex.Message}");
                return Array.Empty<NewsItem>();
            }
        }));
        return results.SelectMany(r => r).OrderByDescending(i => i.Published).Take(60).ToList();
    }

    private async Task<IReadOnlyList<NewsItem>> GetFeedAsync(NewsFeed feed, CancellationToken ct)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(feed.Url, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) return hit.Items;
        }
        var xml = await http.GetStringAsync(feed.Url, ct);
        var items = Parse(xml, feed.Name);
        lock (_cache) _cache[feed.Url] = (DateTimeOffset.UtcNow, items);
        return items;
    }

    public static IReadOnlyList<NewsItem> Parse(string xml, string feedName)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new System.Xml.XmlException("empty feed");
        var entries = root.Name == Atom + "feed"
            ? root.Elements(Atom + "entry").Select(e => FromAtom(e, feedName))
            : root.Descendants("item").Select(e => FromRss(e, feedName));
        return entries.Where(i => i is not null).Select(i => i!).ToList();
    }

    private static NewsItem? FromRss(XElement e, string feed)
    {
        var title = Clean(e.Element("title")?.Value);
        var link = e.Element("link")?.Value.Trim() ?? "";
        if (string.IsNullOrEmpty(title)) return null;
        var description = e.Element("description")?.Value ?? "";
        var content = e.Element(Content + "encoded")?.Value;
        var image = e.Elements("enclosure").FirstOrDefault(x => ((string?)x.Attribute("type") ?? "").StartsWith("image", StringComparison.Ordinal))?.Attribute("url")?.Value
                    ?? e.Element(Media + "content")?.Attribute("url")?.Value
                    ?? e.Element(Media + "thumbnail")?.Attribute("url")?.Value
                    ?? FirstImage(content ?? description);
        return Build(feed, title, link, description, content, image, ParseDate(e.Element("pubDate")?.Value));
    }

    private static NewsItem? FromAtom(XElement e, string feed)
    {
        var title = Clean(e.Element(Atom + "title")?.Value);
        if (string.IsNullOrEmpty(title)) return null;
        var link = e.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value ?? "";
        var summary = e.Element(Atom + "summary")?.Value ?? "";
        var content = e.Element(Atom + "content")?.Value;
        var date = ParseDate(e.Element(Atom + "published")?.Value ?? e.Element(Atom + "updated")?.Value);
        return Build(feed, title, link, summary, content, FirstImage(content ?? summary), date);
    }

    private static NewsItem Build(string feed, string title, string link, string description, string? content, string? image, long published)
    {
        var paragraphs = Paragraphs(content ?? description);
        var summary = Clean(description);
        if (summary.Length > 280) summary = summary[..277].TrimEnd() + " …";
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(link.Length > 0 ? link : title)))[..16];
        return new NewsItem(id, feed, title, summary, link, image is null ? null : HttpsOnly(image), published, paragraphs);
    }

    /// <summary>Article HTML to plain paragraphs for the reader; no markup survives.</summary>
    internal static IReadOnlyList<string> Paragraphs(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var withBreaks = BlockEnd().Replace(html, "\n\n");
        return withBreaks.Split("\n\n")
            .Select(Clean)
            .Where(p => p.Length > 1)
            .Take(60)
            .ToList();
    }

    internal static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = WebUtility.HtmlDecode(Tags().Replace(html, " "));
        return Spaces().Replace(text, " ").Trim();
    }

    private static string? FirstImage(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = ImgSrc().Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    /// <summary>The UI only loads https images; most feeds serve both.</summary>
    private static string HttpsOnly(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    private static long ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var v = value.Trim();
        // RFC 822 dates sometimes carry zone names .NET doesn't know.
        v = ZoneName().Replace(v, m => m.Value.Trim() switch { "GMT" or "UT" or "UTC" => " +0000", "CET" => " +0100", "CEST" => " +0200", _ => m.Value });
        string[] formats = ["ddd, dd MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm:ss zzz", "dd MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm zzz"];
        if (DateTimeOffset.TryParseExact(v.Replace("+0000", "+00:00").Replace("+0100", "+01:00").Replace("+0200", "+02:00"), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact.ToUnixTimeMilliseconds();
        }
        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any) ? any.ToUnixTimeMilliseconds() : 0;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"</(p|div|h[1-6]|li|blockquote)>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<img[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();

    [GeneratedRegex(@"\s(GMT|UTC|UT|CET|CEST)$")]
    private static partial Regex ZoneName();
}
