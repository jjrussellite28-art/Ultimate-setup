using System.Text;
using MegaHub.Core.Config;
using Xunit;

namespace MegaHub.Core.Tests;

/// <summary>config.json is clamped on load: absurd values cannot break the app, and nothing it cannot read ever loosens a protection.</summary>
public sealed class ConfigSanitizeTests
{
    private static AppConfig Load(string json) => ConfigStore.Parse(json)!;

    private static string Profile(string id, string name = "Kid") =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "avatar": "fox", "color": "#3fa9f5", "restricted": true, "data": { "favorites": [], "collections": [], "lastLaunched": {}, "playtimeMinutes": {}, "lastSessionMinutes": {}, "settings": {}, "rules": { "enabled": true, "weekdayMinutes": 60, "weekendMinutes": 60 } } }""";

    [Fact]
    public void Numbers_are_clamped_to_the_ranges_of_the_settings_page()
    {
        var c = Load("""{ "saveLimitsDecimal": true, "returnHoldMs": 5, "screensaverMinutes": 99999, "displayOffMinutes": -3, "setupWizardDone": 2000000, "soundVolume": 500, "musicVolume": -1, "nightModeStrength": 101, "browserZoom": 1, "backupsToKeep": 0, "autoBackupsToKeep": 1000, "autoBackupMaxGameMb": 1, "autoBackupMaxTotalMb": 2000000000 }""");
        Assert.Equal(ConfigLimits.ReturnHoldMsMin, c.ReturnHoldMs);
        Assert.Equal(ConfigLimits.ScreensaverMinutesMax, c.ScreensaverMinutes);
        Assert.Equal(0, c.DisplayOffMinutes);
        Assert.Equal(ConfigLimits.SetupWizardDoneMax, c.SetupWizardDone);
        Assert.Equal(100, c.SoundVolume);
        Assert.Equal(0, c.MusicVolume);
        Assert.Equal(100, c.NightModeStrength);
        Assert.Equal(ConfigLimits.BrowserZoomMin, c.BrowserZoom);
        Assert.Equal(1, c.BackupsToKeep);
        Assert.Equal(MegaHub.Core.Saves.SaveGameService.MaxAutoKeep, c.AutoBackupsToKeep);
        Assert.Equal(MegaHub.Core.Saves.SaveGameService.MinGameMb, c.AutoBackupMaxGameMb);
        Assert.Equal(MegaHub.Core.Saves.SaveGameService.MaxTotalMb, c.AutoBackupMaxTotalMb);
    }

    [Fact]
    public void Values_in_range_stay_untouched()
    {
        var c = Load("""{ "returnHoldMs": 1500, "browserZoom": 200, "couchLock": "code", "remotePort": 9000 }""");
        Assert.Equal(1500, c.ReturnHoldMs);
        Assert.Equal(200, c.BrowserZoom);
        Assert.Equal("code", c.CouchLock);
        Assert.Equal(9000, c.RemotePort);
        Assert.Empty(c.Sanitize());
    }

    [Fact]
    public void Invalid_choices_go_back_to_their_defaults()
    {
        var c = Load("""{ "screensaverStyle": "bogus", "audioMode": "loud", "uiHdr": "x", "padGlyphs": "nintendo", "pointer": "?", "nightMode": "always", "language": "xx", "favoritesMode": "zzz", "favoritesRecentDays": 12345, "integrationMode": "turbo" }""");
        Assert.Equal("art", c.ScreensaverStyle);
        Assert.Equal("ui", c.AudioMode);
        Assert.Equal("auto", c.UiHdr);
        Assert.Equal("auto", c.PadGlyphs);
        Assert.Equal("skin", c.Pointer);
        Assert.Equal("off", c.NightMode);
        Assert.Equal("auto", c.Language);
        Assert.Equal(MegaHub.Core.Library.FavoritesShelf.ModeCombined, c.FavoritesMode);
        Assert.Equal(MegaHub.Core.Library.FavoritesShelf.DefaultRecentDays, c.FavoritesRecentDays);
        Assert.Equal("light", c.IntegrationMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(70000)]
    [InlineData(-5)]
    public void A_bad_port_becomes_the_default_and_its_listener_stays_off(int port)
    {
        var c = Load($$"""{ "remoteEnabled": true, "remotePort": {{port}}, "mcpEnabled": true, "mcpLan": true, "mcpPort": {{port}} }""");
        Assert.Equal(ConfigLimits.DefaultRemotePort, c.RemotePort);
        Assert.Equal(ConfigLimits.DefaultMcpPort, c.McpPort);
        Assert.False(c.RemoteEnabled);
        Assert.False(c.McpEnabled);
        Assert.False(c.McpLan);
    }

    [Fact]
    public void A_good_port_keeps_its_listener_setting()
    {
        var c = Load("""{ "remoteEnabled": true, "remotePort": 8766, "mcpEnabled": true, "mcpPort": 8765 }""");
        Assert.True(c.RemoteEnabled);
        Assert.True(c.McpEnabled);
    }

    [Fact]
    public void Long_lists_and_names_are_cut()
    {
        var devices = string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{ "name": "{{new string('x', 500)}}", "tokenHash": "h{{i}}" }"""));
        var favorites = string.Join(",", Enumerable.Range(0, 6000).Select(i => $"\"f{i}\""));
        var c = Load($$"""{ "remoteDevices": [{{devices}}, null], "favorites": [{{favorites}}], "gameFolders": [{{string.Join(",", Enumerable.Range(0, 300).Select(i => $"\"d{i}\""))}}] }""");
        Assert.Equal(ConfigLimits.MaxRemoteDevices, c.RemoteDevices.Count);
        Assert.All(c.RemoteDevices, d => Assert.True(d.Name.Length <= ConfigLimits.MaxDeviceNameLength));
        Assert.Equal("h0", c.RemoteDevices[0].TokenHash);
        Assert.Equal(ConfigLimits.MaxListEntries, c.Favorites.Count);
        Assert.Equal("f0", c.Favorites[0]);
        Assert.Equal(ConfigLimits.MaxFolderEntries, c.GameFolders.Count);
    }

    [Fact]
    public void Profiles_over_the_limit_keep_the_first_and_the_active_one()
    {
        var ids = Enumerable.Range(1, 12).Select(i => $"kid{i}").ToList();
        var profiles = string.Join(",", ids.Select(id => Profile(id)));
        var c = Load($$"""{ "profiles": [{{profiles}}], "activeProfileId": "kid11" }""");
        Assert.Equal(AppConfig.MaxProfiles - 1, c.Profiles!.Count);
        Assert.Equal("kid1", c.Profiles[0].Id);
        Assert.Contains(c.Profiles, p => p.Id == "kid11");
        Assert.DoesNotContain(c.Profiles, p => p.Id == "kid7");
        Assert.Equal("kid11", c.ActiveProfile.Id);
        Assert.True(c.AllProfiles.Count <= AppConfig.MaxProfiles);
    }

    [Fact]
    public void Profiles_without_an_active_overflow_keep_the_first_ones_in_order()
    {
        var profiles = string.Join(",", Enumerable.Range(1, 10).Select(i => Profile($"kid{i}")));
        var c = Load($$"""{ "profiles": [{{profiles}}] }""");
        Assert.Equal(["kid1", "kid2", "kid3", "kid4", "kid5", "kid6", "kid7"], c.Profiles!.Select(p => p.Id));
    }

    [Fact]
    public void A_long_profile_name_is_shortened()
    {
        var c = Load($$"""{ "profiles": [{{Profile("kid1", new string('n', 200))}}] }""");
        Assert.Equal(ProfileNames.MaxLength, c.Profiles![0].Name.Length);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("OFF ")]
    [InlineData("Passcode")]
    public void A_lock_mode_nobody_can_read_becomes_a_lock_not_off(string mode)
    {
        var c = Load($$"""{ "couchLock": "{{mode}}" }""");
        if (mode.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) Assert.Equal("off", c.CouchLock);
        else Assert.Equal("windows", c.CouchLock);
    }

    [Fact]
    public void A_null_lock_mode_is_a_lock()
    {
        Assert.Equal("windows", Load("""{ "couchLock": null }""").CouchLock);
    }

    [Fact]
    public void A_known_lock_mode_is_kept()
    {
        Assert.Equal("code", Load("""{ "couchLock": "code" }""").CouchLock);
        Assert.Equal("off", Load("""{ "couchLock": "off" }""").CouchLock);
    }

    [Fact]
    public void A_missing_parental_block_means_parental_controls_on()
    {
        var c = Load("""{ "parental": null }""");
        Assert.True(c.ParentalEnabled);
    }

    [Fact]
    public void Negative_play_time_is_never_turned_into_no_limit()
    {
        var c = Load("""{ "parental": { "enabled": true, "weekdayMinutes": -10, "weekendMinutes": -1, "perGameMinutes": -7 } }""");
        Assert.True(c.ParentalEnabled);
        Assert.True(c.Parental.WeekdayMinutes > 0);
        Assert.True(c.Parental.WeekendMinutes > 0);
        Assert.True(c.Parental.PerGameMinutes > 0);
    }

    [Fact]
    public void Absurd_play_time_is_cut_to_a_day_and_locked_games_stay()
    {
        var c = Load("""{ "parental": { "enabled": true, "weekdayMinutes": 99999, "lockedItems": ["a", null, "b"] } }""");
        Assert.Equal(1440, c.Parental.WeekdayMinutes);
        Assert.Equal(["a", "b"], c.Parental.LockedItems);
        Assert.True(c.ParentalEnabled);
    }

    [Fact]
    public void Integration_mode_is_compared_case_insensitively()
    {
        Assert.Equal("full", Load("""{ "integrationMode": "Full" }""").IntegrationMode);
        Assert.Equal("light", Load("""{ "integrationMode": "turbo" }""").IntegrationMode);
    }

    [Fact]
    public void Corrections_are_logged_once_per_file_path()
    {
        var lines = new List<string>();
        var a = new AppConfig { ReturnHoldMs = 1 };
        var b = new AppConfig { ReturnHoldMs = 1 };
        var c = new AppConfig { ReturnHoldMs = 1 };
        var p1 = Path.Combine(Path.GetTempPath(), "a-" + Guid.NewGuid() + ".json");
        var p2 = Path.Combine(Path.GetTempPath(), "b-" + Guid.NewGuid() + ".json");
        ConfigSanitizer.Run(a, p1, lines.Add);
        ConfigSanitizer.Run(b, p1, lines.Add);
        ConfigSanitizer.Run(c, p2, lines.Add);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Sanitizing_twice_changes_nothing()
    {
        var c = Load("""{ "returnHoldMs": 1, "couchLock": "x", "remotePort": 0 }""");
        Assert.Empty(c.Sanitize());
    }
}
