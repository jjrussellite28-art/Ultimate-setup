using CouchHud.Core.Integrations;

namespace CouchHud.Core.Tests;

public class NewsTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "news", name));

    [Fact]
    public void Rss_items_are_parsed_with_text_images_and_dates()
    {
        var items = NewsService.Parse(Fixture("rss.xml"), "Test");

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("Windows 11: Neues Update & mehr", first.Title);
        Assert.Equal("Kurzer Teaser zum Update.", first.Summary);
        Assert.Equal(new[] { "Erster Absatz.", "Zweiter Absatz mit Link ." }, first.Paragraphs);
        Assert.Equal("https://img.example.com/1.jpg", first.Image);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T18:15:00Z").ToUnixTimeMilliseconds(), first.Published);
        Assert.Equal("https://img.example.com/2.jpg", items[1].Image);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T08:00:00Z").ToUnixTimeMilliseconds(), items[1].Published);
        Assert.NotEqual(first.Id, items[1].Id);
    }

    [Fact]
    public void Atom_feeds_work_too()
    {
        var items = NewsService.Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title>Atom Eintrag</title>
                <link rel="alternate" href="https://example.com/a"/>
                <published>2026-10-17T10:00:00Z</published>
                <summary>Zusammenfassung</summary>
              </entry>
            </feed>
            """, "Atom");
        var item = Assert.Single(items);
        Assert.Equal("https://example.com/a", item.Link);
        Assert.Equal("Zusammenfassung", item.Summary);
    }

    [Fact]
    public async Task A_broken_feed_does_not_break_the_others()
    {
        var handler = new Feeds(url => url.Contains("good") ? Fixture("rss.xml") : "<html>not a feed");
        var news = new NewsService(new HttpClient(handler));
        var items = await news.GetAsync([new NewsFeed { Name = "Gut", Url = "https://good" }, new NewsFeed { Name = "Kaputt", Url = "https://bad" }]);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("Gut", i.Feed));
    }

    private sealed class Feeds(Func<string, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body(request.RequestUri!.ToString())) });
    }
}
