namespace CouchHud.Core.Guard;

/// <summary>
/// What may be entered as a "game": a locked couch starts every game without asking, so a shell, a browser or
/// anything from Windows' own folders must never be one (it would be a permanent way out of the couch).
/// </summary>
public static class GameEntryRules
{
    /// <summary>
    /// Interpreters and command-line agents: whoever can start one with parameters can run anything, so they are apps
    /// (unlock needed) and never games (started on a locked couch without asking).
    /// </summary>
    private static readonly string[] Interpreters =
    [
        "claude", "python", "pythonw", "py", "pyw", "node", "deno", "bun", "bash", "sh", "zsh", "wsl", "wslhost", "ssh", "git-bash", "mintty",
        "ruby", "perl", "php", "lua", "dotnet", "powershell", "pwsh", "cmd", "wt", "mshta", "wscript", "cscript"
    ];

    /// <summary>Folders whose programs are part of Windows (System32, SysWOW64 …) and never a game.</summary>
    public static bool InWindowsFolder(string? path, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(windowsDir)) return false;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var root = Path.GetFullPath(windowsDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when this program may be a game. A shell, a tool, a browser (see <see cref="LockedForeground.IsNotGame"/>), a client
    /// (Steam, Epic … are no games but their launchers) or anything inside the Windows folder may not.
    /// </summary>
    public static bool MayBeGame(string? exePath, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var name = Path.GetFileName(exePath.Trim().Trim('"'));
        if (LockedForeground.IsNotGame(name)) return false;
        if (ForegroundPolicy.Classify(name) is not null) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        if (Interpreters.Any(i => string.Equals(i, stem, StringComparison.OrdinalIgnoreCase))) return false;
        return !InWindowsFolder(exePath, windowsDir);
    }

    /// <summary>A link as a "game": web pages (the browser), files and Windows' own tools are no games; store and game protocols are.</summary>
    public static bool MayBeGameUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var t = url.Trim();
        string[] notGames = ["http:", "https:", "file:", "ftp:", "ms-settings:", "ms-windows-store:", "shell:", "mailto:", "search-ms:", "ms-cxh:"];
        return !notGames.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A folder that is watched for games must not be a Windows folder (or inside one) or a whole drive.</summary>
    public static bool MayBeGameFolder(string? folder, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var full = Path.GetFullPath(folder.Trim()).TrimEnd('\\', '/');
            if (Path.GetPathRoot(full + Path.DirectorySeparatorChar) is { } root
                && string.Equals(full, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            var windows = Path.GetFullPath(windowsDir).TrimEnd('\\', '/');
            return !string.Equals(full, windows, StringComparison.OrdinalIgnoreCase)
                && !InWindowsFolder(full + Path.DirectorySeparatorChar + "x", windowsDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
