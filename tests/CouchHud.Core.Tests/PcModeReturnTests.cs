using CouchHud.Core.Guard;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Tests;

public class PcModeReturnTests
{
    [Fact]
    public void Outside_pc_mode_the_couch_rules_apply()
    {
        Assert.Equal(PcModeGuide.NotPcMode, PcModeReturn.OnGuide(pcMode: false, entering: false, guideTapIsOurs: true));
        Assert.Equal(PcModeGuide.NotPcMode, PcModeReturn.OnGuide(pcMode: false, entering: true, guideTapIsOurs: false));
    }

    [Fact]
    public void A_tap_returns_to_the_couch()
    {
        Assert.Equal(PcModeGuide.Return, PcModeReturn.OnGuide(pcMode: true, entering: false, guideTapIsOurs: true));
    }

    [Fact]
    public void A_tap_given_away_still_returns_when_held()
    {
        // Settings › Controller › Guide button in game: "Open Game Bar" or "Hold Guide only".
        Assert.Equal(PcModeGuide.ReturnOnHold, PcModeReturn.OnGuide(pcMode: true, entering: false, guideTapIsOurs: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void While_the_desktop_comes_up_the_button_waits(bool tap)
    {
        Assert.Equal(PcModeGuide.Wait, PcModeReturn.OnGuide(pcMode: true, entering: true, guideTapIsOurs: tap));
    }

    [Theory]
    [InlineData(true, "pcMode.backTap")]
    [InlineData(false, "pcMode.backHold")]
    public void The_hint_says_how_to_come_back(bool tap, string key)
    {
        Assert.Equal(key, PcModeReturn.NoticeKey(tap));
        Assert.True(Strings.German.ContainsKey(key));
        Assert.True(Strings.English.ContainsKey(key));
    }
}
