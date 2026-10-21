using CouchHud.Core.Localization;
using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>What the process start for a library entry looks like: no shell parsing, every part is its own field.</summary>
/// <param name="PackagedAppId">Set for a Store app: started through the activation manager, the shell is the fallback.</param>
public sealed record StartPlan(string FileName, string Arguments, string WorkingDirectory, string? PackagedAppId = null);

/// <summary>Startup parameters and working folder of an entry: parsing, checks and the start plan.</summary>
public static class LaunchOptions
{
    public const int MaxArgumentsLength = 2000;

    /// <summary>One line the user typed ("-d D:\x devtool --resume") as separate arguments; quotes group, nothing else is interpreted.</summary>
    public static IReadOnlyList<string> ParseArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Length > MaxArgumentsLength || text.Any(char.IsControl)) throw new ArgumentException(Strings.Get("entry.argsInvalid"));
        return Split(text);
    }

    /// <summary>The Windows rules (CommandLineToArgvW): quotes group, backslashes only count in front of a quote. The inverse of <see cref="CommandLine.Quote"/>.</summary>
    private static List<string> Split(string text)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var has = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < text.Length && text[i] == '\\') { n++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', n);
                    i--;
                }
                has = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                has = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (has) args.Add(current.ToString());
                current.Clear();
                has = false;
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }
        if (has) args.Add(current.ToString());
        return args;
    }

    /// <summary>The arguments as the user would type them, so editing shows what was saved.</summary>
    public static string FormatArguments(IEnumerable<string> args) => CommandLine.Join(args);

    /// <summary>
    /// The working folder: empty means the default (the program's own folder); otherwise an absolute folder that exists.
    /// Returns the normalized path, or null for the default.
    /// </summary>
    public static string? ValidateWorkingDirectory(string? folder, string exePath, Func<string, bool>? directoryExists = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        directoryExists ??= Directory.Exists;
        var text = folder.Trim().Trim('"');
        string full;
        try
        {
            if (!Path.IsPathRooted(text) || text.Any(char.IsControl)) throw new ArgumentException(Strings.Get("entry.cwdMissing", text));
            full = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(Strings.Get("entry.cwdMissing", text));
        }
        if (!directoryExists(full)) throw new ArgumentException(Strings.Get("entry.cwdMissing", full));
        var own = Path.GetDirectoryName(exePath);
        var same = own is not null && string.Equals(Path.TrimEndingDirectorySeparator(own), Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase);
        return same ? null : full;
    }

    /// <summary>Builds the start. A working folder that has gone away since is an error, not a silent start in the wrong place.</summary>
    public static StartPlan Plan(LaunchTarget target, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        if (target.Kind == LaunchKind.Url)
        {
            var id = InstalledApps.IdOf(target.Target);
            return new StartPlan(target.Target, "", "", InstalledApps.IsPackagedId(id) ? id : null);
        }
        var cwd = target.WorkingDirectory ?? "";
        if (cwd.Length > 0 && !directoryExists(cwd)) throw new InvalidOperationException(Strings.Get("entry.cwdMissing", cwd));
        return new StartPlan(target.Target, CommandLine.Join(target.Args), cwd);
    }
}
