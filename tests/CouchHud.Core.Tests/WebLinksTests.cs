using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public sealed class WebLinksTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://example.com")]
    [InlineData("  HTTPS://Example.com/  ")]
    public void WebAddressesPass(string url) => Assert.True(WebLinks.IsWebUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:network")]
    [InlineData("steam://rungameid/1")]
    [InlineData("shell:AppsFolder\\Foo!App")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://example.com/x")]
    [InlineData("\\\\server\\share\\a.exe")]
    [InlineData("https://trusted.example@evil.example/")]
    [InlineData("https:///nohost")]
    public void EverythingElseIsNotALink(string? url) => Assert.False(WebLinks.IsWebUrl(url));

    [Fact]
    public void AbsurdlyLongAddressesAreRefused() => Assert.False(WebLinks.IsWebUrl("https://example.com/" + new string('a', 5000)));

    [Theory]
    [InlineData("https://www.netflix.com/browse", true)]
    [InlineData("https://netflix.com", true)]
    [InlineData("https://www.disneyplus.com/", true)]
    [InlineData("https://www.primevideo.com/", true)]
    [InlineData("https://www.amazon.de/gp/video/storefront", true)]
    [InlineData("https://play.max.com/", true)]
    [InlineData("https://www.youtube.com/tv", false)]
    [InlineData("https://www.twitch.tv/", false)]
    [InlineData("https://www.amazon.de/dp/B000", false)]
    [InlineData("https://notnetflix.com/", false)]
    [InlineData("https://netflix.com.evil.example/", false)]
    [InlineData("steam://rungameid/1", false)]
    public void KnownCopyProtectedServicesDefaultToEdge(string url, bool edge) => Assert.Equal(edge, WebLinks.DefaultOpenInEdge(url));

    [Fact]
    public void AnEntrysOwnChoiceBeatsTheDefault()
    {
        Assert.False(WebLinks.UsesEdge(new CustomEntry { Name = "N", Url = "https://netflix.com", OpenInEdge = false }));
        Assert.True(WebLinks.UsesEdge(new CustomEntry { Name = "Y", Url = "https://www.youtube.com", OpenInEdge = true }));
        // Not decided yet: the default of the address.
        Assert.True(WebLinks.UsesEdge(new CustomEntry { Name = "N", Url = "https://netflix.com" }));
        Assert.False(WebLinks.UsesEdge(new CustomEntry { Name = "Y", Url = "https://www.youtube.com" }));
        // Only web addresses can go to Edge.
        Assert.False(WebLinks.UsesEdge(new CustomEntry { Name = "S", Url = "steam://rungameid/1", OpenInEdge = true }));
        Assert.False(WebLinks.UsesEdge(new CustomEntry { Name = "P", Path = "C:\\a.exe" }));
    }

    [Fact]
    public void MigrationKeepsEdgeForDrmServicesAndMovesTheRestToTheBuiltInBrowser()
    {
        var entries = new List<CustomEntry>
        {
            new() { Name = "Netflix", Url = "https://www.netflix.com/" },
            new() { Name = "YouTube", Url = "https://www.youtube.com/tv" },
            new() { Name = "Kodi", Path = "C:\\Kodi\\kodi.exe" },
            new() { Name = "Steam game", Url = "steam://rungameid/1" },
            new() { Name = "Chosen", Url = "https://www.twitch.tv/", OpenInEdge = true }
        };
        Assert.Equal(2, WebLinks.Migrate(entries));
        Assert.True(entries[0].OpenInEdge);
        Assert.False(entries[1].OpenInEdge);
        Assert.Null(entries[2].OpenInEdge);
        Assert.Null(entries[3].OpenInEdge);
        Assert.True(entries[4].OpenInEdge);
        // Decided once: running it again changes nothing.
        Assert.Equal(0, WebLinks.Migrate(entries));
    }

    [Fact]
    public void LibraryItemsKnowWhereTheyOpen()
    {
        var items = CustomEntries.From(
        [
            new CustomEntry { Name = "YouTube", Url = "https://www.youtube.com/tv", OpenInEdge = false },
            new CustomEntry { Name = "Netflix", Url = "https://www.netflix.com/", OpenInEdge = true },
            new CustomEntry { Name = "Legacy", Url = "steam://rungameid/7" },
            new CustomEntry { Name = "Program", Path = "C:\\a.exe" }
        ]);
        Assert.True(items[0].Launch.Embedded);
        Assert.False(items[1].Launch.Embedded);
        Assert.False(items[2].Launch.Embedded);
        Assert.Equal(LaunchKind.Exe, items[3].Launch.Kind);
        Assert.False(items[3].Launch.Embedded);
    }
}
