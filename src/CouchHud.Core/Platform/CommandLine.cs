using System.Text;

namespace CouchHud.Core.Platform;

public static class CommandLine
{
    /// <summary>
    /// Splits a Windows command line as found in UninstallString/Run values into
    /// executable and arguments. Handles quoted paths, unquoted paths with spaces
    /// ("C:\Program Files\x\unins.exe /S") and bare commands ("MsiExec.exe /X{...}").
    /// </summary>
    public static (string File, string Arguments) Split(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            var close = s.IndexOf('"', 1);
            if (close > 0) return (s[1..close], s[(close + 1)..].Trim());
            return (s.Trim('"'), "");
        }

        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0 && (exe + 4 == s.Length || char.IsWhiteSpace(s[exe + 4])))
        {
            return (s[..(exe + 4)], s[(exe + 4)..].Trim());
        }

        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    /// <summary>
    /// Quotes one argument so CommandLineToArgvW / the MSVC runtime read it back
    /// unchanged, including embedded quotes and trailing backslashes.
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"')) return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes only need doubling right before a quote.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            sb.Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>Splits an argument string ("-w \"C:\My Saves\" -fast") like CommandLineToArgvW, for the common cases.</summary>
    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
