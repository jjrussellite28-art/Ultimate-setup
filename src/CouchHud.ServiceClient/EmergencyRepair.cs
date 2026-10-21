using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CouchHud.Core.Guard;
using CouchHud.Core.Platform;

namespace CouchHud.ServiceClient;

/// <summary>
/// A program to start elevated (UAC) with its exact argument string, in <see cref="WorkingDirectory"/> (System32: not a folder of the
/// user, where cmd.exe would look first for anything started by a bare name).
/// </summary>
public sealed record ElevatedLaunch(string FileName, string Arguments, string WorkingDirectory);

/// <summary>
/// The only administrator paths besides the service (owner decision E11), both started by the user, both with Windows' own UAC prompt,
/// both native Windows programs with a fixed command, never CouchHud.exe:
/// <list type="bullet">
/// <item>"Repair the service": the installer's repair script <see cref="RepairScriptName"/> in the protected program folder (P8).</item>
/// <item>"Remove stuck restrictions" (what the service cannot release): <c>reg.exe delete</c> for the values of the
/// <see cref="PolicyGroups"/> CouchHUD has evidence for that still hold the lock's value, in the account's own hive (HKEY_USERS\&lt;SID&gt;), chained by
/// <c>cmd.exe /d</c> (no AutoRun) so there is one prompt. Every part of the command is checked against a strict pattern.</item>
/// </list>
/// Pure: builds the commands; the shell starts them.
/// </summary>
public static class EmergencyRepair
{
    /// <summary>
    /// The repair script the installer puts next to CouchHud.exe (P8: a batch file that re-creates and starts the service with
    /// sc.exe; exit code 0 means the service runs). Referenced by name only (the same value as CouchHud.Privileged.ServiceInstall's
    /// RepairScript); when it is missing, the UI says so and points to the setup.
    /// </summary>
    public const string RepairScriptName = "repair-service.cmd";

    private static readonly Regex SafeText = new(@"^[A-Za-z0-9 _.\\-]{1,200}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SystemDirectory = new(@"^[A-Za-z]:\\[A-Za-z0-9 _.\\-]{1,200}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Digits = new(@"^[0-9]{1,4}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// System32\cmd.exe /d (no AutoRun) /c ""&lt;app&gt;\<see cref="RepairScriptName"/>"" (P8's launch line): the script by full path in
    /// the protected program folder. A path with a character cmd would interpret (quote, percent, caret, ampersand, pipe, angle
    /// brackets) is refused. The caller checks that the folder is protected and that the environment carries no runtime hooks.
    /// </summary>
    public static ElevatedLaunch RepairService(string systemDirectory, string programDirectory)
    {
        var script = Path.Combine(programDirectory, RepairScriptName);
        if (!SystemDirectory.IsMatch(systemDirectory)) throw new ArgumentException("unexpected system directory", nameof(systemDirectory));
        if (script.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '!', '\r', '\n']) >= 0) throw new ArgumentException("a character cmd would interpret in the path", nameof(programDirectory));
        return new ElevatedLaunch(Path.Combine(systemDirectory, "cmd.exe"), $"/d /c \"\"{script}\"\"", systemDirectory);
    }

    /// <summary>
    /// An administrator's approval (P9 review M-c): <c>&lt;system folder&gt;\sc.exe control CouchHud &lt;code&gt;</c>, started with UAC. Windows'
    /// own consent prompt on the secure desktop asks the administrator; the credential never passes through CouchHUD. The code (128 to 255) is
    /// the one the service gave for the pending request; only administrators may send it (the service's access list).
    /// </summary>
    public static ElevatedLaunch AdminApproval(string systemDirectory, string serviceName, int control)
    {
        if (!SystemDirectory.IsMatch(systemDirectory)) throw new ArgumentException("unexpected system directory", nameof(systemDirectory));
        if (control is < 128 or > 255) throw new ArgumentOutOfRangeException(nameof(control));
        if (!SafeText.IsMatch(serviceName) || serviceName.Contains(' ')) throw new ArgumentException("unexpected service name", nameof(serviceName));
        return new ElevatedLaunch(Path.Combine(systemDirectory, "sc.exe"), $"control {serviceName} {control}", systemDirectory);
    }

    /// <summary>One value to delete: the key below HKCU (as <see cref="PolicyGroups"/> names it) and the value's name.</summary>
    public sealed record StuckValue(string Key, string Name);

    /// <summary>
    /// The values of the lock that are still set with the lock's own data, in the groups CouchHUD has evidence for
    /// (<paramref name="groups"/>: the ids in the mark file, or groups reported stuck with a sign that CouchHUD set them): the DWORDs that
    /// hold exactly the group's value, and the entries of the DisallowRun lists that name one of the programs the lock blocks. A value with
    /// other data is never touched. The registry cannot tell who wrote a value with the same data (the user or a Group Policy may have set
    /// exactly that), which is why only groups with such evidence are looked at.
    /// </summary>
    public static IReadOnlyList<StuckValue> StuckValues(IRegistry registry, IReadOnlyCollection<string> groups)
    {
        var values = new List<StuckValue>();
        foreach (var group in PolicyGroups.All.Where(g => groups.Contains(g.Id, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (var change in group.Dwords)
            {
                if (registry.ReadDword(change.Location, change.Name)?.ToString(CultureInfo.InvariantCulture) == change.Value) values.Add(new StuckValue(change.Location, change.Name));
            }
            foreach (var key in group.ListKeys)
            {
                foreach (var (name, data) in registry.ReadValues(key))
                {
                    if (LockHardening.BlockedPrograms.Contains(data.Trim(), StringComparer.OrdinalIgnoreCase)) values.Add(new StuckValue(key, name));
                }
            }
        }
        return values.Distinct().ToList();
    }

    /// <summary>
    /// cmd.exe /d /s /c "reg.exe delete "HKU\SID\…" /v Name /f &amp; …" for the values. Throws when a SID, key or name does not match
    /// its pattern (nothing of the command comes from text that could carry a quote, an ampersand or a percent sign). Null when there
    /// is nothing to delete.
    /// </summary>
    public static ElevatedLaunch? StuckPolicyCleanup(string systemDirectory, string sid, IReadOnlyList<StuckValue> values)
    {
        if (values.Count == 0) return null;
        if (!PolicyGroups.IsUserSid(sid)) throw new ArgumentException("not a user SID", nameof(sid));
        if (!SystemDirectory.IsMatch(systemDirectory)) throw new ArgumentException("unexpected system directory", nameof(systemDirectory));
        var reg = Path.Combine(systemDirectory, "reg.exe");
        var command = new StringBuilder();
        foreach (var value in values)
        {
            if (!value.Key.StartsWith(@"HKCU\Software\", StringComparison.OrdinalIgnoreCase) || !SafeText.IsMatch(value.Key)) throw new ArgumentException($"unexpected key {value.Key}");
            // DisallowRun entries are numbered; the DWORDs have plain names. Both only letters, digits, dot, dash, underscore.
            if (!SafeText.IsMatch(value.Name) || value.Name.Contains('\\') || value.Name.Contains(' ')) throw new ArgumentException($"unexpected value name {value.Name}");
            if (!PolicyGroups.All.Any(g => g.Changes.Any(c => c.Location.Equals(value.Key, StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException($"not a key of the lock: {value.Key}");
            var isListKey = PolicyGroups.All.Any(g => g.ListKeys.Contains(value.Key, StringComparer.OrdinalIgnoreCase));
            var isDword = PolicyGroups.All.Any(g => g.Dwords.Any(c => c.Location.Equals(value.Key, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)));
            if (!isDword && !(isListKey && Digits.IsMatch(value.Name))) throw new ArgumentException($"not a value of the lock: {value.Name}");
            if (command.Length > 0) command.Append(" & ");
            command.Append('"').Append(reg).Append("\" delete \"HKU\\").Append(sid).Append('\\').Append(value.Key[5..]).Append("\" /v ").Append(value.Name).Append(" /f");
        }
        // cmd's own limit is 8191 characters; the lock has far fewer values than that would allow.
        if (command.Length > 7000) throw new ArgumentException("command too long");
        return new ElevatedLaunch(Path.Combine(systemDirectory, "cmd.exe"), $"/d /s /c \"{command}\"", systemDirectory);
    }
}
