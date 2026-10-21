namespace CouchHud.Core.Platform;

public static class DirectorySize
{
    /// <summary>
    /// Sums all file sizes below <paramref name="dir"/>. Skips folders we may not read
    /// and junctions (no double counting). Returns 0 if the folder doesn't exist,
    /// null if <paramref name="ct"/> fired before it finished.
    /// </summary>
    public static long? Measure(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        long total = 0;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            if (ct.IsCancellationRequested) return null;
            total += file.Length;
        }
        return total;
    }
}
