using System.Text;

namespace MegaHub.Core.Updates;

/// <summary>
/// Where winget.exe is, as a full path, before anything is started. A bare "winget" would let Windows search the program's own
/// folder and the current directory first; and PATH may not list WindowsApps at all (a fresh profile, a shell started without the
/// user's environment), although the App Execution Alias is there. So: the absolute, local entries of PATH in order (relative
/// ones, "." and UNC shares are skipped, never the working directory), then <c>%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe</c>.
/// <para>
/// Only a file that exists counts, and only one whose path starts with one of the protected roots (Windows or the program folders,
/// which includes the package folder of App Installer; a prefix check on the normalized path, it does not look at the ACLs) or the
/// App Execution Alias in the user's WindowsApps folder: that one is a reparse point of the alias kind which Windows resolves to the
/// package, so a plain file planted there is not taken.
/// </para>
/// <para>
/// What this does not cover: the user's own WindowsApps folder can be written by the same user, so code that already runs as that
/// user could swap the alias between this check and the start (and could as well change PATH or the user's files). That is the
/// same-user boundary, no gain in rights: Mega Hub runs as the couch user and winget elevates only through Windows' own prompt.
/// The image that actually starts is not checked again. Pure: the file checks come from the caller (tests).
/// </para>
/// </summary>
public static class WingetLocator
{
    public const string FileName = "winget.exe";

    /// <summary>The full path of winget.exe, or null when there is none that may be used.</summary>
    /// <param name="protectedRoots">Folders only administrators can write to (Windows, Program Files, Program Files (x86)).</param>
    /// <param name="exists">True for a file (not a folder) at this full path.</param>
    /// <param name="isAlias">True when the file at this full path is an App Execution Alias (a reparse point of that kind, not a file).</param>
    public static string? Resolve(string? path, string? localAppData, IEnumerable<string?> protectedRoots, Func<string, bool> exists, Func<string, bool> isAlias)
    {
        var roots = protectedRoots.Select(Clean).OfType<string>().Select(r => r.TrimEnd('\\') + "\\").ToList();
        var aliasDir = AliasFolder(localAppData);
        foreach (var dir in Candidates(path, localAppData))
        {
            var full = Join(dir, FileName);
            var inAliasFolder = aliasDir is not null && string.Equals(dir.TrimEnd('\\'), aliasDir, StringComparison.OrdinalIgnoreCase);
            var inProtected = roots.Any(r => (dir.TrimEnd('\\') + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase));
            if (!inAliasFolder && !inProtected) continue;
            if (!exists(full)) continue;
            if (inAliasFolder && !inProtected && !isAlias(full)) continue;
            return full;
        }
        return null;
    }

    /// <summary>The folders looked at, in order, each once.</summary>
    public static IReadOnlyList<string> Candidates(string? path, string? localAppData)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (path ?? "").Split(';'))
        {
            var dir = Clean(raw);
            if (dir is not null && seen.Add(dir.TrimEnd('\\'))) result.Add(dir);
        }
        if (AliasFolder(localAppData) is { } alias && seen.Add(alias)) result.Add(alias);
        return result;
    }

    private static string? AliasFolder(string? localAppData) =>
        Clean(localAppData) is { } local ? local.TrimEnd('\\') + @"\Microsoft\WindowsApps" : null;

    /// <summary>An absolute local folder ("C:\..."), unquoted; null for relative entries, ".", UNC or device paths and anything odd.</summary>
    internal static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var dir = raw.Trim();
        if (dir.Length >= 2 && dir[0] == '"' && dir[^1] == '"') dir = dir[1..^1].Trim();
        if (dir.Length is < 3 or > 260) return null;
        // Drive letter, colon, backslash: nothing relative ("C:foo" is relative to that drive's current folder), no \\server or \\?\.
        if (!char.IsAsciiLetter(dir[0]) || dir[1] != ':' || dir[2] is not ('\\' or '/')) return null;
        if (dir.Any(c => c < ' ' || c is '"' or '<' or '>' or '|' or '*' or '?')) return null;
        if (dir.IndexOf(':', 2) >= 0) return null; // no alternate data streams
        var normalized = dir.Replace('/', '\\');
        // "." and ".." segments would leave the named folder (and fool the prefix check): such an entry is not taken.
        if (normalized.Split('\\').Any(s => s is ".." or ".")) return null;
        return normalized;
    }

    private static string Join(string dir, string file) => dir.EndsWith('\\') ? dir + file : dir + "\\" + file;
}

/// <summary>Builds a Windows command line from a fixed list of arguments, quoted the way the C runtime reads them back (CommandLineToArgvW rules).</summary>
public static class CommandLine
{
    /// <summary>The program (in quotes) and each argument, quoted when needed. Throws for a quote in the program or a NUL anywhere.</summary>
    public static string Build(string program, IEnumerable<string> args)
    {
        if (program.Contains('"') || program.Contains('\0')) throw new ArgumentException("program path with a quote", nameof(program));
        var sb = new StringBuilder();
        sb.Append('"').Append(program).Append('"');
        foreach (var a in args)
        {
            sb.Append(' ');
            Append(sb, a);
        }
        return sb.ToString();
    }

    /// <summary>One argument: as is when it has no blank, tab or quote, else in quotes with backslashes doubled before a quote or the end.</summary>
    internal static void Append(StringBuilder sb, string arg)
    {
        if (arg.Contains('\0')) throw new ArgumentException("argument with NUL", nameof(arg));
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            // Backslashes before a quote are doubled and the quote escaped; elsewhere they stay as they are.
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        sb.Append('"');
    }
}
