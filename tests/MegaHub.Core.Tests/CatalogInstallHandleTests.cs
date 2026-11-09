using System.Net;
using System.Security.Cryptography;
using MegaHub.Core.Plugins;

namespace MegaHub.Core.Tests;

/// <summary>The downloaded package stays on one handle created new; every failure leaves no temp file and no open handle behind.</summary>
[Collection("Language")]
public class CatalogInstallHandleTests : IDisposable
{
    private static readonly SemVer App = new(0, 3, 0, "");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "megahub-handle-" + Guid.NewGuid().ToString("N"));

    public CatalogInstallHandleTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private CatalogService Service(byte[] body) => new(new HttpClient(new FakeHandler(body)), _dir, () => [], () => false);

    private static CatalogEntry Skin(string sha) =>
        new("s", "s", "", null, CatalogKind.Skin, "1.0.0", null, "https://example.com/s.zip", sha, null, null, "s", "https://example.com/i.json", null);

    private string[] TempFiles() => Directory.Exists(Path.Combine(_dir, "catalog-temp")) ? Directory.GetFiles(Path.Combine(_dir, "catalog-temp")) : [];

    [Fact]
    public async Task A_checksum_mismatch_leaves_no_temp_file_and_no_open_handle()
    {
        var body = "not what the catalog promised"u8.ToArray();
        await Assert.ThrowsAsync<CatalogException>(() => Service(body).InstallAsync(Skin(new string('0', 64)), App));
        // A handle left open would have made the delete fail and left the file behind.
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task A_bad_archive_with_the_right_hash_leaves_no_temp_file_and_no_open_handle()
    {
        var body = "this is not a zip file, but its hash matches"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(body));
        var ex = await Assert.ThrowsAsync<CatalogException>(() => Service(body).InstallAsync(Skin(sha), App));
        Assert.Contains("rchiv", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void A_file_that_is_already_at_the_download_name_fails_closed_and_is_not_touched()
    {
        var path = Path.Combine(_dir, "planted.zip");
        File.WriteAllText(path, "planted");
        Assert.ThrowsAny<IOException>(() => CatalogService.CreateDownloadFile(path));
        Assert.Equal("planted", File.ReadAllText(path));
    }

    [Fact]
    public void The_download_file_cannot_be_written_or_replaced_by_others_while_it_is_open()
    {
        var path = Path.Combine(_dir, "download.zip");
        using var file = CatalogService.CreateDownloadFile(path);
        file.Write([1, 2, 3]);
        file.Flush();
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "swapped"));
        Assert.ThrowsAny<IOException>(() => File.Delete(path));
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // reading is fine
        Assert.Equal(3, reader.Length);
    }

    [Fact]
    public void An_install_without_an_expected_hash_is_refused()
    {
        var path = Path.Combine(_dir, "x.zip");
        File.WriteAllText(path, "x");
        var service = Service([]);
        Assert.Throws<CatalogException>(() => service.Install(Skin(new string('0', 64)), path, null, ""));
        Assert.Throws<CatalogException>(() => service.Install(Skin(new string('0', 64)), path, null, "  "));
        Assert.Throws<CatalogException>(() => service.Install(Skin(new string('0', 64)), path, null, null!));
    }
}
