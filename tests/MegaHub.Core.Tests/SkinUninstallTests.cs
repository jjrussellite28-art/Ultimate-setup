using MegaHub.Core.Config;
using MegaHub.Core.Plugins;
using MegaHub.Core.Skins;

namespace MegaHub.Core.Tests;

/// <summary>
/// Uninstalling a catalog skin: only a folder directly below skins goes (nothing outside it, no link, never the Couch skin's id), the
/// record of the install goes with it, and whoever had the skin gets the Couch skin.
/// </summary>
[Collection("Language")]
public class SkinUninstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "megahub-skin-uninstall-" + Guid.NewGuid().ToString("N"));

    public SkinUninstallTests() => Directory.CreateDirectory(Path.Combine(_dir, "skins"));

    public void Dispose()
    {
        // A junction left in the skins folder is taken off first (a recursive delete would stop at it).
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(_dir, "skins")))
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, recursive: false);
        Directory.Delete(_dir, recursive: true);
    }

    private string Skin(string folder)
    {
        var dir = Path.Combine(_dir, "skins", folder);
        Directory.CreateDirectory(Path.Combine(dir, "sounds"));
        File.WriteAllText(Path.Combine(dir, "skin.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "skin.css"), ":root{}");
        File.WriteAllText(Path.Combine(dir, "sounds", "a.ogg"), "x");
        return dir;
    }

    [Fact]
    public void An_installed_skin_is_removed_with_everything_in_it_and_leaves_the_others()
    {
        var gone = Skin("nova");
        var kept = Skin("deepsea");
        Assert.True(CatalogService.UninstallSkin(_dir, "nova"));
        Assert.False(Directory.Exists(gone));
        Assert.True(Directory.Exists(kept));
        Assert.DoesNotContain(SkinCatalog.ListUserSkins(Path.Combine(_dir, "skins"), "en"), s => s.Id == "user:nova");
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "skins"), PackageInstaller.TempPrefix + "*"));
    }

    [Fact]
    public void A_skin_that_is_not_there_counts_as_removed()
    {
        Assert.True(CatalogService.UninstallSkin(_dir, "never-installed"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData("../plugins")]
    [InlineData("..\\plugins")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:\\Windows")]
    [InlineData("/etc")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".hidden")]
    [InlineData("nova.")]
    [InlineData("nova..")]
    [InlineData("nova ")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void A_name_that_is_no_catalog_folder_or_is_the_Couch_skin_is_refused_and_nothing_is_touched(string folder)
    {
        var plugins = Path.Combine(_dir, "plugins", "x");
        Directory.CreateDirectory(plugins);
        var skin = Skin("nova");
        Assert.Throws<ArgumentException>(() => CatalogService.UninstallSkin(_dir, folder));
        Assert.True(Directory.Exists(plugins));
        Assert.True(Directory.Exists(skin));
        Assert.True(Directory.Exists(Path.Combine(_dir, "skins")));
    }

    [Fact]
    public void A_link_in_place_of_a_skin_folder_is_not_followed_or_deleted()
    {
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var link = Path.Combine(_dir, "skins", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // no right to make links here: nothing to test
        }
        Assert.Throws<ArgumentException>(() => CatalogService.UninstallSkin(_dir, "linked"));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void The_install_record_goes_with_the_skin_and_names_the_folder()
    {
        Skin("Nova-Folder");
        var registry = new CatalogRegistry(_dir);
        registry.Set(CatalogRegistry.Key(CatalogKind.Skin, "nova"), new CatalogRegistry.Record("1.0.0", "Nova-Folder", false));
        Assert.Equal("Nova-Folder", CatalogService.SkinFolderOf(_dir, "nova"));
        Assert.Equal("unknown", CatalogService.SkinFolderOf(_dir, "unknown"));
        Assert.True(CatalogService.UninstallSkin(_dir, "Nova-Folder"));
        Assert.DoesNotContain(CatalogRegistry.Key(CatalogKind.Skin, "nova"), registry.Read().Keys);
    }

    [Fact]
    public void The_household_and_every_profile_that_had_the_skin_fall_back_to_the_Couch_skin()
    {
        var config = new AppConfig { Skin = "user:nova" };
        var profile = config.AddProfile("Kid");
        profile.Skin = "user:nova";
        var other = config.AddProfile("Other");
        other.Skin = "user:deepsea";
        Assert.True(ConfigStore.ForgetSkin(config, "user:nova"));
        Assert.Equal("default", config.Skin);
        Assert.Equal("default", config.Profile(profile.Id).Skin);
        Assert.Equal("user:deepsea", config.Profile(other.Id).Skin);
        Assert.False(ConfigStore.ForgetSkin(config, "user:nova"));
    }

    [Fact]
    public void An_old_id_of_the_skin_is_met_too_and_the_Couch_skin_is_not_touched()
    {
        var config = new AppConfig { Skin = "nova" };
        Assert.True(ConfigStore.ForgetSkin(config, "user:nova"));
        Assert.Equal("default", config.Skin);
        Assert.False(ConfigStore.ForgetSkin(config, "user:nova"));
        Assert.Equal("default", config.Skin);
    }

    private static bool Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void A_junction_inside_the_skin_is_removed_without_touching_what_it_points_to()
    {
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var skin = Skin("nova");
        if (!Junction(Path.Combine(skin, "sounds", "jump"), target)) return; // not Windows: nothing to test
        Assert.True(CatalogService.UninstallSkin(_dir, "nova"));
        Assert.False(Directory.Exists(skin));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "skins")));
    }

    [Fact]
    public void A_junction_in_place_of_the_skin_folder_is_refused_and_its_target_survives()
    {
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        if (!Junction(Path.Combine(_dir, "skins", "jlink"), target)) return;
        Assert.Throws<ArgumentException>(() => CatalogService.UninstallSkin(_dir, "jlink"));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void A_skin_that_cannot_be_moved_now_stays_with_its_record_and_the_call_says_so()
    {
        if (!OperatingSystem.IsWindows()) return;
        var skin = Skin("nova");
        new CatalogRegistry(_dir).Set(CatalogRegistry.Key(CatalogKind.Skin, "nova"), new CatalogRegistry.Record("1.0.0", "nova", false));
        using (new FileStream(Path.Combine(skin, "sounds", "a.ogg"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(CatalogService.UninstallSkin(_dir, "nova"));
        }
        Assert.True(File.Exists(Path.Combine(skin, "skin.json")));
        Assert.Contains(CatalogRegistry.Key(CatalogKind.Skin, "nova"), new CatalogRegistry(_dir).Read().Keys);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "skins"), PackageInstaller.TempPrefix + "*"));
        Assert.True(CatalogService.UninstallSkin(_dir, "nova"));
        Assert.False(Directory.Exists(skin));
    }

    [Fact]
    public void Leftovers_of_an_uninstall_are_deleted_later_but_a_link_among_them_is_not_followed()
    {
        var left = Path.Combine(_dir, "skins", PackageInstaller.TempPrefix + "gone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(left);
        File.WriteAllText(Path.Combine(left, "skin.css"), "x");
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var linkOnly = Path.Combine(_dir, "skins", PackageInstaller.TempPrefix + "gone-" + Guid.NewGuid().ToString("N"));
        var linked = Junction(linkOnly, target);
        CatalogService.ClearSkinLeftovers(_dir);
        Assert.False(Directory.Exists(left));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        if (linked) Assert.True(Directory.Exists(linkOnly));
    }

    [Fact]
    public void A_read_only_file_does_not_keep_the_skin_from_going()
    {
        var skin = Skin("nova");
        var file = Path.Combine(skin, "sounds", "a.ogg");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Assert.True(CatalogService.UninstallSkin(_dir, "nova"));
        Assert.False(Directory.Exists(skin));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "skins")));
    }

    private static void Deep(string skin, int levels)
    {
        var path = skin;
        for (var i = 0; i < levels; i++) path = Path.Combine(path, "d");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "end.txt"), "x");
    }

    [Fact]
    public void A_tree_within_the_depth_cap_goes_completely()
    {
        var skin = Skin("nova");
        Deep(skin, 60);
        Assert.True(CatalogService.UninstallSkin(_dir, "nova"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "skins")));
    }

    [Fact]
    public void A_tree_deeper_than_the_cap_is_left_hidden_and_never_overflows_the_stack()
    {
        var skin = Skin("nova");
        Deep(skin, 200);
        Assert.True(CatalogService.UninstallSkin(_dir, "nova"));
        Assert.DoesNotContain(SkinCatalog.ListUserSkins(Path.Combine(_dir, "skins"), "en"), s => s.Id == "user:nova");
        var left = Directory.EnumerateDirectories(Path.Combine(_dir, "skins"), PackageInstaller.TempPrefix + "gone-*").ToList();
        Assert.Single(left);
        Assert.False(Directory.Exists(skin));
    }

    [Fact]
    public void The_folder_is_the_one_on_disk_and_the_config_reset_ignores_case()
    {
        Skin("Nova");
        Assert.Equal("Nova", CatalogService.SkinFolderOnDisk(_dir, "nova"));
        Assert.Equal("other", CatalogService.SkinFolderOnDisk(_dir, "other"));
        Assert.Throws<ArgumentException>(() => CatalogService.SkinFolderOnDisk(_dir, ".."));
        var config = new AppConfig { Skin = "user:Nova" };
        Assert.True(ConfigStore.ForgetSkin(config, "user:nova"));
        Assert.Equal("default", config.Skin);
    }

    [Fact]
    public void The_bridge_and_the_host_keep_the_gate_the_install_has()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var hud = File.ReadAllText(Path.Combine(root, "src", "MegaHub.Shell", "Hud.cs"));
        var at = hud.IndexOf("public async Task<object> UninstallSkinAsync(string id)", StringComparison.Ordinal);
        Assert.True(at > 0);
        var body = hud[at..Math.Min(hud.Length, at + 900)];
        Assert.True(body.IndexOf("UpdateOrThrow", StringComparison.Ordinal) < body.IndexOf("CatalogService.UninstallSkin", StringComparison.Ordinal), "the settings are saved before anything is deleted");
        Assert.True(body.IndexOf("RequireSkinChange();", StringComparison.Ordinal) is > 0 && body.IndexOf("RequireSkinChange();", StringComparison.Ordinal) < body.IndexOf("CatalogService.UninstallSkin", StringComparison.Ordinal));
        Assert.DoesNotContain("uninstallSkin", LockPolicy_OpenWhileLocked());
    }

    private static IEnumerable<string> LockPolicy_OpenWhileLocked() => MegaHub.Core.Guard.LockPolicy.OpenWhileLocked;
}
