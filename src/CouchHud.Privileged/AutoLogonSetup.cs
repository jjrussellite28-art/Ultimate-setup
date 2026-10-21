using Microsoft.Win32;
using CouchHud.Core.Security;

namespace CouchHud.Privileged;

/// <summary>
/// The administrator side of the automatic sign-in, set up like Sysinternals Autologon: the password becomes the LSA secret
/// "DefaultPassword" (encrypted, readable only by the system), Winlogon gets the account, and a machine-wide marker remembers that
/// CouchHud did it (so "off" and the uninstaller put the earlier state back). The password only ever lives in the buffer
/// that <see cref="TurnOn"/> is given; nothing here logs it.
/// </summary>
public sealed class AutoLogonSetup(ILsaSecrets lsa) : IAutoLogon
{
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string PasswordLessKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";
    private const string MarkerKey = @"SOFTWARE\CouchHud";
    private const string MarkerValue = "AutoLogonSetUpBy";
    private const string PasswordLessBackup = "DevicePasswordLessBackup";
    // The names Winlogon had before CouchHud wrote its account (saved once, put back by "off").
    private const string PreviousUser = "WinlogonPreviousUserName";
    private const string PreviousDomain = "WinlogonPreviousDomainName";
    private const string PreviousSaved = "WinlogonPreviousSaved";

    /// <summary>Exit code of the helper when Windows refused to store the secret.</summary>
    public const int ExitSecretFailed = 3;

    public static (bool Enabled, string User, string Domain) Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonKey);
            return (key?.GetValue("AutoAdminLogon") as string == "1", key?.GetValue("DefaultUserName") as string ?? "", key?.GetValue("DefaultDomainName") as string ?? "");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Fail-closed (P9 review H2): what cannot be read counts as an automatic sign-in that is on.
            return (true, "", "");
        }
    }

    /// <summary>
    /// Machine-wide note that CouchHud set the automatic sign-in up (the config flag lives in
    /// one user's profile; the uninstaller may run as someone else).
    /// </summary>
    public static bool SetUpByCouchHud()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MarkerKey);
            return key?.GetValue(MarkerValue) is string s && s.Length > 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    AutoLogonInfo IAutoLogon.Read()
    {
        var (enabled, user, domain) = Read();
        return new AutoLogonInfo(enabled, user, domain);
    }

    string? IAutoLogon.SetUpBy() => SetUpByAccount();

    /// <summary>The account the machine-wide marker names (<c>DOMAIN\user</c>), null without one.</summary>
    public static string? SetUpByAccount()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MarkerKey);
            return key?.GetValue(MarkerValue) is string s && s.Length > 0 ? s : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Only allow Windows Hello sign-in for Microsoft accounts" makes Windows ignore a stored
    /// password for automatic sign-in.
    /// </summary>
    public static bool PasswordlessOnly()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PasswordLessKey);
            return key?.GetValue("DevicePasswordLessBuildVersion") is int v && v == 2;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Switches the automatic sign-in on for the account: the secret first, then the marker, the backup and Winlogon. Takes over the
    /// password buffer and wipes it. 0 when done, 1 without a password, <see cref="ExitSecretFailed"/> when Windows refused the secret.
    /// </summary>
    public int TurnOn(char[] chars, string user, string domain)
    {
        try
        {
            if (chars.Length == 0) return 1;
            // The secret first: a failure must not leave "on" without a password behind.
            if (!lsa.SetDefaultPassword(chars)) return ExitSecretFailed;
        }
        finally
        {
            Array.Clear(chars);
        }

        try
        {
            // Marker and backup first: whatever fails later, "off" and the uninstaller still find their work.
            using (var marker = Registry.LocalMachine.CreateSubKey(MarkerKey))
            {
                marker.SetValue(MarkerValue, $@"{domain}\{user}");
                SavePreviousNames(marker);
                using var device = Registry.LocalMachine.OpenSubKey(PasswordLessKey, writable: true);
                if (device?.GetValue("DevicePasswordLessBuildVersion") is int v && v == 2)
                {
                    // Remember the Hello-only setting so "off" and the uninstaller can put it back.
                    marker.SetValue(PasswordLessBackup, 2, RegistryValueKind.DWord);
                    device.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
                }
            }
            using var winlogon = Registry.LocalMachine.CreateSubKey(WinlogonKey);
            winlogon.SetValue("DefaultUserName", user);
            winlogon.SetValue("DefaultDomainName", domain);
            // A plain-text password here would be readable by everyone; the secret replaces it.
            winlogon.DeleteValue("DefaultPassword", throwOnMissingValue: false);
            winlogon.DeleteValue("AutoLogonCount", throwOnMissingValue: false);
            winlogon.SetValue("AutoAdminLogon", "1");
        }
        catch
        {
            // No orphaned secret without a switch that explains it.
            lsa.DeleteDefaultPassword();
            throw;
        }
        return 0;
    }

    /// <summary>The first turn-on remembers the user and domain name Winlogon had (none is remembered as none).</summary>
    private static void SavePreviousNames(RegistryKey marker)
    {
        if (marker.GetValue(PreviousSaved) is not null) return;
        using var winlogon = Registry.LocalMachine.OpenSubKey(WinlogonKey);
        if (winlogon?.GetValue("DefaultUserName") as string is { } previousUser) marker.SetValue(PreviousUser, previousUser);
        if (winlogon?.GetValue("DefaultDomainName") as string is { } previousDomain) marker.SetValue(PreviousDomain, previousDomain);
        marker.SetValue(PreviousSaved, 1, RegistryValueKind.DWord);
    }

    /// <summary>"Off" gives the earlier names back, so Windows does not keep showing the couch account as the last user.</summary>
    private static void RestorePreviousNames(RegistryKey marker)
    {
        var wrote = marker.GetValue(MarkerValue) as string ?? "";
        var slash = wrote.IndexOf('\\');
        var (domain, user) = slash >= 0 ? (wrote[..slash], wrote[(slash + 1)..]) : ("", wrote);
        var saved = marker.GetValue(PreviousSaved) is not null;
        using (var winlogon = Registry.LocalMachine.CreateSubKey(WinlogonKey))
        {
            Put(winlogon, "DefaultUserName", WinlogonNames.Restore(winlogon.GetValue("DefaultUserName") as string, user, marker.GetValue(PreviousUser) as string, saved));
            Put(winlogon, "DefaultDomainName", WinlogonNames.Restore(winlogon.GetValue("DefaultDomainName") as string, domain, marker.GetValue(PreviousDomain) as string, saved));
        }
        foreach (var name in new[] { PreviousUser, PreviousDomain, PreviousSaved }) marker.DeleteValue(name, throwOnMissingValue: false);

        static void Put(RegistryKey key, string name, WinlogonRestore restore)
        {
            if (restore.Kind == WinlogonRestoreKind.Write) key.SetValue(name, restore.Value!);
            else if (restore.Kind == WinlogonRestoreKind.Delete) key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>Releases the "Windows Hello only" mode (the password sign-in works again).</summary>
    public void AllowPasswordSignIn()
    {
        using var device = Registry.LocalMachine.CreateSubKey(PasswordLessKey);
        device.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
    }

    public int TurnOff()
    {
        using (var winlogon = Registry.LocalMachine.CreateSubKey(WinlogonKey))
        {
            winlogon.SetValue("AutoAdminLogon", "0");
            winlogon.DeleteValue("DefaultPassword", throwOnMissingValue: false);
            winlogon.DeleteValue("AutoLogonCount", throwOnMissingValue: false);
        }
        // The marker stays when the secret could not be deleted: the next uninstall tries again.
        if (!lsa.DeleteDefaultPassword()) return ExitSecretFailed;
        using (var machine = Registry.LocalMachine.OpenSubKey(MarkerKey, writable: true))
        {
            if (machine?.GetValue(PasswordLessBackup) is int backup)
            {
                using var device = Registry.LocalMachine.CreateSubKey(PasswordLessKey);
                device.SetValue("DevicePasswordLessBuildVersion", backup, RegistryValueKind.DWord);
                machine.DeleteValue(PasswordLessBackup, throwOnMissingValue: false);
            }
            if (machine is not null) RestorePreviousNames(machine);
            machine?.DeleteValue(MarkerValue, throwOnMissingValue: false);
            if (machine is { SubKeyCount: 0, ValueCount: 0 })
            {
                machine.Close();
                Registry.LocalMachine.DeleteSubKey(MarkerKey, throwOnMissingSubKey: false);
            }
        }
        return 0;
    }
}
