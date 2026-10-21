using CouchHud.Core.Library;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

public class DeckFeatureTests
{
    private static string Manifest(int flags, long done = 0, long total = 0) => $$"""
        "AppState"
        {
            "appid"		"1245620"
            "name"		"ELDEN RING"
            "StateFlags"		"{{flags}}"
            "BytesToDownload"		"{{total}}"
            "BytesDownloaded"		"{{done}}"
        }
        """;

    [Fact]
    public void Installed_games_are_no_downloads()
    {
        Assert.Null(SteamScanner.ParseDownload(Manifest(4)));
    }

    [Fact]
    public void Running_update_with_progress()
    {
        var d = SteamScanner.ParseDownload(Manifest(4 | 2 | 1024 | 1048576, 250, 1000))!;
        Assert.Equal(SteamDownloadState.Downloading, d.State);
        Assert.Equal(0.25, d.Progress);
        Assert.True(d.IsUpdate);
    }

    [Fact]
    public void Paused_first_install_and_queued_update()
    {
        var paused = SteamScanner.ParseDownload(Manifest(2 | 512, 10, 100))!;
        Assert.Equal(SteamDownloadState.Paused, paused.State);
        Assert.False(paused.IsUpdate);
        Assert.Equal(SteamDownloadState.Queued, SteamScanner.ParseDownload(Manifest(4 | 2))!.State);
    }

    [Theory]
    [InlineData("off", 21, false)]
    [InlineData("on", 12, true)]
    [InlineData("evening", 21, true)]
    [InlineData("evening", 6, true)]
    [InlineData("evening", 12, false)]
    public void Night_mode_schedule(string mode, int hour, bool active)
    {
        Assert.Equal(active, NightLight.IsActive(mode, new TimeOnly(hour, 0)));
    }

    [Fact]
    public void Night_ramp_keeps_red_and_warms_blue()
    {
        var ramp = NightLight.Ramp(100);
        Assert.Equal(65535, ramp[255]);
        Assert.True(ramp[512 + 255] < ramp[256 + 255]);
        Assert.Equal(new ushort[] { 0, 257 }, NightLight.Ramp(0)[..2]);
        Assert.Equal(65535, NightLight.Ramp(0)[512 + 255]);
    }
}
