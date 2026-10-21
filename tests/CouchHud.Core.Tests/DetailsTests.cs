using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class DetailsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Game\\unins000.exe\" /SILENT", "C:\\Program Files\\Game\\unins000.exe", "/SILENT")]
    [InlineData("C:\\Program Files\\Foo\\uninst.exe /S", "C:\\Program Files\\Foo\\uninst.exe", "/S")]
    [InlineData("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", "MsiExec.exe", "/X{12345678-1234-1234-1234-123456789012}")]
    [InlineData("C:\\Games\\uninstall.exe", "C:\\Games\\uninstall.exe", "")]
    [InlineData("  \"C:\\x\\a.exe\"  ", "C:\\x\\a.exe", "")]
    [InlineData("rundll32.exe foo.dll,Bar", "rundll32.exe", "foo.dll,Bar")]
    public void CommandLine_split_handles_uninstall_strings(string input, string file, string args)
    {
        var (f, a) = CommandLine.Split(input);
        Assert.Equal(file, f);
        Assert.Equal(args, a);
    }

    [Fact]
    public void Store_details_are_parsed_and_cleaned()
    {
        using var doc = JsonDocument.Parse("""
            {
              "1245620": {
                "success": true,
                "data": {
                  "short_description": "Erhebe dich, <b>Befleckter</b> &amp; werde zum Eldenfürsten.",
                  "developers": ["FromSoftware Inc."],
                  "publishers": ["Bandai Namco"],
                  "release_date": { "coming_soon": false, "date": "25. Feb. 2022" },
                  "genres": [ { "id": "1", "description": "Action" }, { "id": "3", "description": "Rollenspiel" } ],
                  "movies": [
                    { "id": 1, "mp4": { "480": "http://video.example/480.mp4", "max": "http://video.example/max.mp4" } },
                    { "id": 2, "webm": { "max": "https://video.example/only.webm" } }
                  ],
                  "screenshots": [ { "id": 0, "path_full": "https://shots.example/1.jpg" } ]
                }
              }
            }
            """);

        var info = StoreInfoService.Parse(doc.RootElement, "1245620")!;

        Assert.Equal("Erhebe dich, Befleckter & werde zum Eldenfürsten.", info.Description);
        Assert.Equal(new[] { "FromSoftware Inc." }, info.Developers);
        Assert.Equal("25. Feb. 2022", info.ReleaseDate);
        Assert.Equal(new[] { "Action", "Rollenspiel" }, info.Genres);
        Assert.Equal(new[] { "https://video.example/max.mp4" }, info.Trailers);
        Assert.Equal(new[] { "https://shots.example/1.jpg" }, info.Screenshots);
    }

    [Fact]
    public void Store_details_without_success_are_null()
    {
        using var doc = JsonDocument.Parse("""{ "42": { "success": false } }""");
        Assert.Null(StoreInfoService.Parse(doc.RootElement, "42"));
    }

    [Fact]
    public async Task Store_info_matches_non_steam_items_by_name_and_caches()
    {
        var calls = 0;
        var handler = new Handler(url =>
        {
            calls++;
            if (url.Contains("storesearch")) return """{ "items": [ { "id": 1145360, "name": "Hades" } ] }""";
            if (url.Contains("appdetails?appids=1145360")) return """{ "1145360": { "success": true, "data": { "short_description": "Trotze dem Gott der Toten." } } }""";
            return null;
        });
        var cache = Path.Combine(Directory.CreateTempSubdirectory("couchhud-store-").FullName, "store.json");
        var item = new LibraryItem
        {
            Id = "epic:hades",
            Source = "epic",
            Name = "Hades",
            Launch = new LaunchTarget(LaunchKind.Url, "x://")
        };

        var info = await new StoreInfoService(new HttpClient(handler), cache).GetAsync(item);
        Assert.Equal("Trotze dem Gott der Toten.", info!.Description);
        Assert.Equal(2, calls);

        var again = await new StoreInfoService(new HttpClient(handler), cache).GetAsync(item);
        Assert.Equal("Trotze dem Gott der Toten.", again!.Description);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Directory_size_sums_files_recursively()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-size-").FullName;
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[100]);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), new byte[23]);

        Assert.Equal(123, DirectorySize.Measure(dir, default));
        Assert.Equal(0, DirectorySize.Measure(Path.Combine(dir, "missing"), default));
    }

    private sealed class Handler(Func<string, string?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = respond(request.RequestUri!.ToString());
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
