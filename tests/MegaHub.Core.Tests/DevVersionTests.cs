using System.Text.RegularExpressions;
using MegaHub.Core.Plugins;
using MegaHub.Core.Updates;

namespace MegaHub.Core.Tests;

/// <summary>
/// A local build once carried the number of the release (0.6.3) and hid the update. Now every build that is not a release build is a
/// pre-release of the number (0.6.4-dev+commit), which is older than the release; a release build is exactly the number.
/// </summary>
public class DevVersionTests
{
    private static string Props()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(file)) return File.ReadAllText(file);
        }
        throw new FileNotFoundException("Directory.Build.props");
    }

    private static ReleaseInfo Rel(SemVer v, bool pre) => new($"v{v}", v, pre, "", null, null, null, null, 0, null, null, null);

    [Fact]
    public void Version_in_props_is_plain_and_the_dev_suffix_is_conditional()
    {
        var props = Props();
        // sign-release.ps1 and the CI tag check read this element: no suffix, ever.
        var m = Regex.Match(props, "<Version>([^<]+)</Version>");
        Assert.True(m.Success);
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.Groups[1].Value);
        // Dev is the default; only an explicit release build drops it.
        Assert.Contains("<InformationalVersion Condition=\"'$(MegaHubRelease)' != 'true'\">$(Version)-dev</InformationalVersion>", props);
    }

    [Fact]
    public void Running_build_is_the_number_of_the_props_as_release_or_dev()
    {
        var number = Regex.Match(Props(), "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.True(SemVer.TryParse(number, out var release));
        var app = CatalogRules.AppVersion;
        Assert.Equal((release.Major, release.Minor, release.Patch), (app.Major, app.Minor, app.Patch));
        Assert.True(app.Pre is "" or "dev");
        if (app.Pre == "dev") Assert.True(app < release);
        else Assert.Equal(release, app);
    }

    [Fact]
    public void Dev_is_older_than_its_release_equal_to_itself_and_newer_than_the_last_release()
    {
        Assert.True(SemVer.TryParse("0.6.4-dev+f627fe74", out var dev));
        Assert.True(SemVer.TryParse("0.6.4", out var release));
        Assert.True(SemVer.TryParse("0.6.4+f627fe74", out var releaseBuild));
        Assert.True(SemVer.TryParse("0.6.3", out var last));
        Assert.True(dev < release);
        Assert.True(dev > last);
        Assert.Equal(release, releaseBuild);
        Assert.Equal(0, release.CompareTo(releaseBuild));
        Assert.Equal(0, dev.CompareTo(dev));
        // Two dev builds of one number are the same version for the updater (the part after the plus is not compared).
        Assert.True(SemVer.TryParse("0.6.4-dev+aaaaaaaa", out var other));
        Assert.Equal(0, dev.CompareTo(other));
    }

    [Fact]
    public void Updater_offers_the_release_to_a_dev_build_and_nothing_to_a_release_build()
    {
        Assert.True(SemVer.TryParse("0.6.4-dev+f627fe74", out var dev));
        Assert.True(SemVer.TryParse("0.6.4+f627fe74", out var releaseBuild));
        var release = Rel(new SemVer(0, 6, 4, ""), false);
        Assert.True(SelfUpdateRules.IsUpdate(release, dev));
        Assert.False(SelfUpdateRules.IsUpdate(release, releaseBuild));
        // A dev build is not an update for itself even when it is published as a pre-release.
        Assert.False(SelfUpdateRules.IsUpdate(Rel(dev, true), dev));
    }

    [Fact]
    public void Release_workflow_marks_tag_builds_as_release_builds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "build.yml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var yml = File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", "build.yml"));
        Assert.Contains("MegaHubRelease: ${{ startsWith(github.ref, 'refs/tags/v') }}", yml);
    }
}
