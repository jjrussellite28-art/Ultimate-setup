namespace CouchHud.Core.Platform;

/// <summary>
/// File and folder names from Windows paths. System.IO.Path only knows the separator of
/// the platform it runs on; Core reads Windows paths from registry and manifests and is
/// tested on other systems too, so both separators count here.
/// </summary>
public static class WinPath
{
    private static int LastSeparator(string path) => path.LastIndexOfAny(['\\', '/']);

    public static string FileName(string path) => path[(LastSeparator(path) + 1)..];

    public static string FileNameWithoutExtension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>The folder part; "C:\" for a file in a drive root, null without a folder (like Path).</summary>
    public static string? DirectoryName(string path)
    {
        var cut = LastSeparator(path);
        if (cut < 0) return null;
        // Keep the separator after the drive letter: "C:\game.exe" → "C:\".
        return cut == 2 && path[1] == ':' ? path[..3] : path[..cut];
    }
}
