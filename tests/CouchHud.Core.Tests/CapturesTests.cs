using CouchHud.Core.Files;

namespace CouchHud.Core.Tests;

public class CapturesTests
{
    [Fact]
    public void Finds_images_and_videos_newest_first_without_steam_thumbnails()
    {
        var root = Path.Combine(Path.GetTempPath(), "couchhud-captures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gamebar = Directory.CreateDirectory(Path.Combine(root, "Captures")).FullName;
            var steam = Directory.CreateDirectory(Path.Combine(root, "steam", "screenshots", "thumbnails")).Parent!.FullName;
            void Write(string path, DateTime time)
            {
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, time);
            }
            Write(Path.Combine(gamebar, "Elden Ring 2026-10-01.mp4"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
            Write(Path.Combine(gamebar, "notes.txt"), new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
            Write(Path.Combine(steam, "20261003_1.jpg"), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
            Write(Path.Combine(steam, "thumbnails", "20261003_1.jpg"), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));

            var found = Captures.Find([(gamebar, "Game Bar"), (Path.Combine(root, "steam"), "Steam"), (Path.Combine(root, "missing"), "x")]);

            Assert.Equal(2, found.Count);
            Assert.Equal("Steam", found[0].Source);
            Assert.False(found[0].Video);
            Assert.True(found[1].Video);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
