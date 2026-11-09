namespace MegaHub.Core.Plugins;

/// <summary>What the lookup of a native library named by a plugin found in the plugin's own folder.</summary>
public enum NativeLookup
{
    /// <summary>A file of the checked snapshot: it may be loaded.</summary>
    Snapshot,

    /// <summary>A file with that name lies in the plugin folder but was not part of what was hashed: never loaded.</summary>
    Refused,

    /// <summary>Nothing of that name in the plugin folder.</summary>
    NotInFolder
}

/// <summary>
/// The rule for native libraries of a plugin (<c>DllImport</c> and the load context): a library in the plugin's folder is loaded only when it
/// was hashed with the rest of the folder; a file with such a name that appeared afterwards is refused for good, whatever else is on the
/// search path. Anything that is not in the folder may only be a system library (by plain name, from System32) for other developers'
/// plugins. Pure, so the rule is tested without a native file.
/// </summary>
public static class PluginNativeRules
{
    /// <summary>The file names the runtime would try for a library name.</summary>
    public static IReadOnlyList<string> Candidates(string name)
    {
        var list = new List<string> { name };
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) list.Add(name + ".dll");
        return list;
    }

    /// <summary>A plain library name: no directory part, no drive, no stream or wildcard characters.</summary>
    public static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 128 && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 && name.Trim('.', ' ').Length > 0;

    /// <param name="resolved">The path the dependency resolver (deps.json) names for the library, or null.</param>
    public static (NativeLookup Result, string? Path) Locate(string name, string folder, string? resolved, Func<string, bool> inSnapshot, Func<string, bool> exists)
    {
        var paths = new List<string>();
        if (resolved is not null) paths.Add(resolved);
        foreach (var candidate in Candidates(name))
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(folder, candidate));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (NativeLookup.Refused, null);
            }
            // A name that leads out of the plugin folder is not looked for there (and is no plain system name either).
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) paths.Add(full);
        }
        string? listed = null;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (inSnapshot(path)) listed ??= path;
            else if (exists(path)) return (NativeLookup.Refused, null);
        }
        return listed is null ? (NativeLookup.NotInFolder, null) : (NativeLookup.Snapshot, listed);
    }
}
