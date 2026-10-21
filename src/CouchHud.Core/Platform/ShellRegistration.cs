namespace CouchHud.Core.Platform;

/// <summary>
/// Reads the Winlogon "Shell" command the way Windows would and compares it exactly.
/// A substring test like Contains("couchhud") also matches a build folder or someone else's
/// "CouchHud-old" copy, and a wrong shell entry means a black screen at the next login.
/// </summary>
public static class ShellRegistration
{
    public const string ExeName = "CouchHud.exe";

    /// <summary>The program of a command line: quoted, or unquoted up to ".exe".</summary>
    public static string? ExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return text[..(exe + 4)];
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>True when the command starts exactly this program (case-insensitive, any separator).</summary>
    public static bool PointsTo(string? command, string exePath) =>
        ExePath(command) is { } exe && Normalize(exe).Equals(Normalize(exePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the command starts a CouchHud.exe, wherever it lives (for cleanup).</summary>
    public static bool NamesCouchHud(string? command) =>
        ExePath(command) is { } exe && WinPath.FileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the file lies inside the folder (not the folder itself, no ".." tricks).</summary>
    public static bool IsUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (f.Length == 0 || p.Split('\\').Contains("..")) return false;
        return p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
