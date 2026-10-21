using System.IO.Compression;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

public class PackageInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-pkg-" + Guid.NewGuid().ToString("N"));

    public PackageInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var file = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open());
            writer.Write(content);
        }
        return file;
    }

    [Fact]
    public void Skin_in_a_top_folder_is_installed_under_that_name()
    {
        var zip = Zip("download.zip", ("Synthwave/skin.json", "{}"), ("Synthwave/skin.css", ":root{}"));

        var target = PackageInstaller.Install(zip, Path.Combine(_dir, "skins"), PackageInstaller.Kind.Skin);

        Assert.Equal(Path.Combine(_dir, "skins", "Synthwave"), target);
        Assert.True(File.Exists(Path.Combine(target, "skin.css")));
    }

    [Fact]
    public void Flat_plugin_uses_the_zip_name_and_replaces_an_older_version()
    {
        var root = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(Path.Combine(root, "Kodi"));
        File.WriteAllText(Path.Combine(root, "Kodi", "old.txt"), "x");
        var zip = Zip("Kodi.zip", ("Kodi.dll", "MZ"), ("plugin.json", "{}"));

        var target = PackageInstaller.Install(zip, root, PackageInstaller.Kind.Plugin);

        Assert.True(File.Exists(Path.Combine(target, "Kodi.dll")));
        Assert.False(File.Exists(Path.Combine(target, "old.txt")));
    }

    [Fact]
    public void Wrong_content_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => PackageInstaller.Install(Zip("a.zip", ("readme.txt", "hi")), _dir, PackageInstaller.Kind.Skin));
        Assert.Throws<InvalidOperationException>(() => PackageInstaller.Install(Zip("b.zip", ("lib/x.dll", "MZ"), ("readme.txt", "")), _dir, PackageInstaller.Kind.Plugin));
        Assert.Throws<ArgumentException>(() => PackageInstaller.Install(Path.Combine(_dir, "missing.zip"), _dir, PackageInstaller.Kind.Skin));
    }

    [Fact]
    public void Plugin_tool_names_are_prefixed_and_safe()
    {
        Assert.Equal("plugin_wand_open_cheats", PluginManager.ToolName("wand", "open-cheats"));
        Assert.Equal("plugin_signal_rgb_set_effect", PluginManager.ToolName("Signal.RGB", "set effect"));
    }
}
