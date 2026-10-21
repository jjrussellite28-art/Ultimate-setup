namespace CouchHud.Core.Files;

public sealed record Capture(string Path, string Source, bool Video, long Time, long Size);

/// <summary>
/// Screenshots and clips from the usual places: Xbox Game Bar, Steam, the Windows
/// screenshot folder and NVIDIA. Newest first.
/// </summary>
public static class Captures
{
    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly string[] VideoTypes = [".mp4", ".webm"];

    /// <param name="folders">Folder and the label to show, e.g. ("…\Videos\Captures", "Game Bar").</param>
    public static IReadOnlyList<Capture> Find(IEnumerable<(string Folder, string Source)> folders, int max = 300)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
        var list = new List<Capture>();
        foreach (var (folder, source) in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    var ext = file.Extension.ToLowerInvariant();
                    var video = VideoTypes.Contains(ext);
                    if (!video && !ImageTypes.Contains(ext)) continue;
                    // Steam keeps small thumbnails next to the real screenshots.
                    if (file.DirectoryName?.EndsWith("thumbnails", StringComparison.OrdinalIgnoreCase) == true) continue;
                    list.Add(new Capture(file.FullName, source, video, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), file.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[captures] {folder}: {ex.Message}");
            }
        }
        return list.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).OrderByDescending(c => c.Time).Take(max).ToList();
    }
}
