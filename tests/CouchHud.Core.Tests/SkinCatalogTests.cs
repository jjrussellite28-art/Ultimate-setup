using CouchHud.Core.Skins;

namespace CouchHud.Core.Tests;

public class SkinCatalogTests
{
    [Fact]
    public void Lists_valid_user_skins_only()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-skins-").FullName;
        var neon = Directory.CreateDirectory(Path.Combine(dir, "neon")).FullName;
        File.WriteAllText(Path.Combine(neon, "skin.json"), """{ "name": "Neon", "glyphs": { "A": "✕" } }""");
        File.WriteAllText(Path.Combine(neon, "skin.css"), ":root[data-skin=\"user:neon\"] { --accent: hotpink; }");

        var noCss = Directory.CreateDirectory(Path.Combine(dir, "nocss")).FullName;
        File.WriteAllText(Path.Combine(noCss, "skin.json"), "{}");

        var broken = Directory.CreateDirectory(Path.Combine(dir, "broken")).FullName;
        File.WriteAllText(Path.Combine(broken, "skin.json"), "{ nope");
        File.WriteAllText(Path.Combine(broken, "skin.css"), "");

        var skin = Assert.Single(SkinCatalog.ListUserSkins(dir));
        Assert.Equal("user:neon", skin.Id);
        Assert.Equal("Neon", skin.Name);
        Assert.Equal("✕", skin.Glyphs["A"]);
        Assert.StartsWith("file:///", skin.Css);
    }

    [Fact]
    public void Missing_folder_means_no_skins() =>
        Assert.Empty(SkinCatalog.ListUserSkins(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
}

public class SkinSoundTests
{
    [Fact]
    public void Only_known_events_and_audio_files_inside_the_skin_count()
    {
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "select.ogg"), "x");
            File.WriteAllText(Path.Combine(dir, "evil.exe"), "x");
            var sounds = CouchHud.Core.Skins.SkinCatalog.SoundsOf(dir, new Dictionary<string, string>
            {
                ["select"] = "select.ogg",
                ["move"] = "missing.wav",
                ["back"] = "evil.exe",
                ["notify"] = "../outside.wav",
                ["explode"] = "select.ogg"
            });
            Assert.Equal(new[] { "select" }, sounds.Keys.ToArray());
            Assert.StartsWith("file:", sounds["select"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
