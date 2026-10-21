using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

public class UpdateSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-safety-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public UpdateSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- marker

    [Fact]
    public void Marker_counts_while_young_and_the_setup_lives()
    {
        UpdateMarker.Write(_dir, 4242, Now);
        Assert.True(UpdateMarker.IsActive(_dir, Now.AddSeconds(30), pid => pid == 4242));
        Assert.True(UpdateMarker.IsActive(_dir, Now.AddMinutes(14), pid => true));
        Assert.False(UpdateMarker.IsActive(_dir, Now.AddMinutes(15), pid => true));       // too old
        Assert.False(UpdateMarker.IsActive(_dir, Now.AddMinutes(-1), pid => true));       // from the future
        Assert.False(UpdateMarker.IsActive(_dir, Now.AddSeconds(30), pid => false));      // setup is gone
        Assert.False(UpdateMarker.IsActive(_dir, Now.AddSeconds(30), pid => pid == 1));   // another process
    }

    [Fact]
    public void Machine_wide_marker_in_the_program_folder_counts_like_the_user_one()
    {
        var program = Path.Combine(_dir, "program");
        var user = Path.Combine(_dir, "user");
        Directory.CreateDirectory(user);
        Assert.False(UpdateMarker.IsActiveAny([program, user], Now, _ => true));   // none there
        UpdateMarker.Write(program, 4242, Now);
        Assert.True(UpdateMarker.IsActiveAny([program, user], Now.AddSeconds(5), pid => pid == 4242));
        Assert.False(UpdateMarker.IsActiveAny([program, user], Now.AddSeconds(5), _ => false));     // setup is gone
        Assert.False(UpdateMarker.IsActiveAny([program, user], Now.AddMinutes(20), _ => true));     // stale
        File.WriteAllText(Path.Combine(program, UpdateMarker.FileName), "setup");                  // the old marker format
        Assert.False(UpdateMarker.IsActiveAny([program, user], Now, _ => true));
    }

    [Fact]
    public void Marker_time_comes_from_the_content_not_from_the_file_time()
    {
        UpdateMarker.Write(_dir, 4242, Now);
        // Touching the file doesn't make an old marker young again.
        File.SetLastWriteTimeUtc(Path.Combine(_dir, UpdateMarker.FileName), Now.AddHours(1).UtcDateTime);
        Assert.False(UpdateMarker.IsActive(_dir, Now.AddHours(1), _ => true));
        // And an old file time doesn't age a fresh one.
        File.SetLastWriteTimeUtc(Path.Combine(_dir, UpdateMarker.FileName), Now.AddDays(-3).UtcDateTime);
        Assert.True(UpdateMarker.IsActive(_dir, Now.AddMinutes(1), _ => true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("setup")]
    [InlineData("12345")]
    [InlineData("-5 100")]
    [InlineData("1792411200000 0")]
    [InlineData("1792411200000 abc")]
    [InlineData("1792411200000 12 34")]
    public void Garbage_or_pidless_marker_is_not_a_running_update(string content)
    {
        File.WriteAllText(Path.Combine(_dir, UpdateMarker.FileName), content);
        Assert.False(UpdateMarker.IsActive(_dir, Now, _ => true));
    }

    [Fact]
    public void Missing_marker_and_clear()
    {
        Assert.False(UpdateMarker.IsActive(_dir, Now, _ => true));
        UpdateMarker.Write(_dir, 7, Now);
        UpdateMarker.Clear(_dir);
        Assert.False(UpdateMarker.IsActive(_dir, Now, _ => true));
        UpdateMarker.Clear(_dir);   // twice is fine
    }

    // ---- paths

    [Fact]
    public void Clean_directory_is_recreated_empty()
    {
        var folder = Path.Combine(_dir, "update-temp");
        Directory.CreateDirectory(Path.Combine(folder, "old"));
        File.WriteAllText(Path.Combine(folder, "old", "x.exe"), "x");
        PathSafety.PrepareCleanDirectory(folder);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void A_file_in_the_way_is_replaced_by_a_folder()
    {
        var folder = Path.Combine(_dir, "update-temp");
        File.WriteAllText(folder, "not a folder");
        PathSafety.PrepareCleanDirectory(folder);
        Assert.True(Directory.Exists(folder));
    }

    /// <summary>Junctions need no privileges (mklink /J); on other systems the test has nothing to check.</summary>
    private bool TryJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        Directory.CreateDirectory(target);
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        p!.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void A_junction_in_the_path_is_refused_and_removed_without_touching_its_target()
    {
        var target = Path.Combine(_dir, "elsewhere");
        var link = Path.Combine(_dir, "update-temp");
        if (!TryJunction(link, target)) return;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");

        Assert.Throws<CatalogException>(() => PathSafety.RequireNoReparsePoints(Path.Combine(link, "setup.exe")));
        Assert.Throws<CatalogException>(() => PathSafety.RequireNoReparsePoints(link));

        PathSafety.PrepareCleanDirectory(link);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));      // the target is untouched
        Assert.False(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));   // now a real folder
        PathSafety.RequireNoReparsePoints(Path.Combine(link, "setup.exe"));
    }

    [Fact]
    public void Plain_paths_pass_and_missing_ones_are_fine()
    {
        PathSafety.RequireNoReparsePoints(Path.Combine(_dir, "a", "b", "c.exe"));
    }

    [Fact]
    public void The_handle_path_must_be_the_expected_one()
    {
        var file = Path.Combine(_dir, "setup.exe");
        PathSafety.RequireSamePath(file, @"\\?\" + file);
        PathSafety.RequireSamePath(file, @"\\?\" + file.ToUpperInvariant());
        Assert.Throws<CatalogException>(() => PathSafety.RequireSamePath(file, @"\\?\" + Path.Combine(_dir, "elsewhere", "setup.exe")));
        Assert.Equal(@"C:\x", PathSafety.StripPrefix(@"\\?\C:\x"));
        Assert.Equal(@"\\server\share\x", PathSafety.StripPrefix(@"\\?\UNC\server\share\x"));
        Assert.Equal(@"C:\x", PathSafety.StripPrefix(@"C:\x"));
    }

    // ---- check: rate limit details, persistence

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static string ReleaseJson() =>
        """{ "tag_name": "v0.4.0", "draft": false, "prerelease": false, "body": "x", "assets": [] }""";

    private static readonly SemVer App = new(0, 3, 0, "");

    [Fact]
    public async Task Only_a_real_rate_limit_403_starts_a_pause()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) });
        var service = new SelfUpdateService(new HttpClient(handler), _dir, () => Now);

        var first = await service.CheckAsync(App);   // 403 without rate-limit headers: an error, no pause
        Assert.NotNull(first.Error);
        Assert.False(first.UpdateAvailable);
        var second = await service.CheckAsync(App);  // asked again at once
        Assert.True(second.UpdateAvailable);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_pause_is_capped_at_24_hours()
    {
        var clock = Now;
        var handler = new FakeHandler(_ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("Retry-After", "9999999");
            return limited;
        });
        var service = new SelfUpdateService(new HttpClient(handler), _dir, () => clock);
        await service.CheckAsync(App);
        Assert.Equal(1, handler.Calls);

        clock = Now.AddHours(23);
        await service.CheckAsync(App);
        Assert.Equal(1, handler.Calls);   // still paused
        clock = Now.AddHours(24).AddMinutes(1);
        await service.CheckAsync(App);
        Assert.Equal(2, handler.Calls);   // after 24 hours at the latest
    }

    [Fact]
    public async Task A_manual_check_waits_at_most_a_minute_for_a_pause()
    {
        var clock = Now;
        var handler = new FakeHandler(_ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Reset", Now.AddMinutes(50).ToUnixTimeSeconds().ToString());
            return limited;
        });
        var service = new SelfUpdateService(new HttpClient(handler), _dir, () => clock);
        await service.CheckAsync(App);
        Assert.Equal(1, handler.Calls);

        clock = Now.AddSeconds(20);
        var tooSoon = await service.CheckAsync(App, manual: true);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("Limit", tooSoon.Error);

        clock = Now.AddSeconds(70);
        await service.CheckAsync(App, manual: true);
        Assert.Equal(2, handler.Calls);

        clock = Now.AddMinutes(10);
        await service.CheckAsync(App);   // the automatic check keeps waiting for GitHub's reset
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void The_announced_version_survives_a_restart()
    {
        var first = new SelfUpdateService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), _dir);
        Assert.Null(first.Announced);
        first.MarkAnnounced("0.4.0");
        var afterRestart = new SelfUpdateService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), _dir);
        Assert.Equal("0.4.0", afterRestart.Announced);
    }

    // ---- download time limit

    /// <summary>Ten bytes every 20 ms, for ever; honours the cancellation token.</summary>
    private sealed class SlowStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(20, ct);
            return Math.Min(10, buffer.Length);
        }
    }

    [Fact]
    public async Task A_trickling_download_hits_the_overall_limit()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string setup = "CouchHud-Setup-0.4.0.exe";
        const string baseUrl = "https://github.com/kewe87/CouchHUD/releases/download/v0.4.0/";
        var sum = $"{new string('a', 64)}  {setup}\n";
        var sig = Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(CatalogSignature.ReleaseContext, Encoding.UTF8.GetBytes(sum)), HashAlgorithmName.SHA256));
        var handler = new FakeHandler(r => r.RequestUri!.AbsoluteUri switch
        {
            baseUrl + setup + ".sha256" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sum) },
            baseUrl + setup + ".sha256.sig" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sig) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream()) }
        });
        var service = new SelfUpdateService(new HttpClient(handler), _dir, null, key.ExportSubjectPublicKeyInfoPem());
        var release = ReleaseParser.Parse($$"""
            { "tag_name": "v0.4.0", "assets": [
              { "name": "{{setup}}", "size": 0, "browser_download_url": "{{baseUrl}}{{setup}}" },
              { "name": "{{setup}}.sha256", "browser_download_url": "{{baseUrl}}{{setup}}.sha256" },
              { "name": "{{setup}}.sha256.sig", "browser_download_url": "{{baseUrl}}{{setup}}.sha256.sig" } ] }
            """)!;
        var old = SelfUpdateService.DownloadLimit;
        SelfUpdateService.DownloadLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(release, Path.Combine(_dir, "dl")));
            Assert.Contains("zu lange", ex.Message);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "dl")));
        }
        finally
        {
            SelfUpdateService.DownloadLimit = old;
        }
    }
}
