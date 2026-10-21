using CouchHud.Core.Platform;
using CouchHud.Core.Security;

namespace CouchHud.Privileged;

// The edges to Windows that the service's operations (lock policies, automatic sign-in, its own state folder) need on top of
// Abstractions.cs. Each has one Windows implementation here and fakes in the tests.

/// <summary>One user's registry hive, open for as long as this lives (a hive the service loaded itself is unloaded on dispose).</summary>
public interface IUserHive : IDisposable
{
    /// <summary>The hive, with keys written as <c>HKCU\…</c> (they land in <c>HKEY_USERS\&lt;SID or mount name&gt;\…</c>).</summary>
    IRegistry Registry { get; }
}

/// <summary>The registry hives of user accounts below HKEY_USERS.</summary>
public interface IUserHives
{
    /// <summary>The hive of a signed-in account (loaded by Windows as <c>HKEY_USERS\&lt;SID&gt;</c>); null when it is not loaded or the SID is no user SID.</summary>
    IUserHive? OpenLoaded(string sid);

    /// <summary>
    /// The loaded hive, or else the account's NTUSER.DAT loaded for the duration (RegLoadKey, only for administrator paths such as
    /// ReleaseAllAccounts): the profile path comes from the machine's ProfileList, and a file that is a link or a reparse point is
    /// refused. Null when neither works.
    /// </summary>
    IUserHive? OpenOrLoad(string sid);
}

/// <summary>What Winlogon says about the automatic sign-in.</summary>
public sealed record AutoLogonInfo(bool Enabled, string User, string Domain);

/// <summary>The machine-wide automatic sign-in (see <see cref="AutoLogonSetup"/>).</summary>
public interface IAutoLogon
{
    AutoLogonInfo Read();

    /// <summary>The account (<c>DOMAIN\user</c>) the machine-wide marker names as set up by CouchHUD; null when there is none.</summary>
    string? SetUpBy();

    /// <summary>On for the account: takes over the password buffer and wipes it. 0 when done (see <see cref="AutoLogonSetup.TurnOn"/>).</summary>
    int TurnOn(char[] password, string user, string domain);

    /// <summary>Off, the earlier names back. 0 when done.</summary>
    int TurnOff();

    /// <summary>Releases the "Windows Hello only" mode.</summary>
    void AllowPasswordSignIn();
}

/// <summary>Account names from SIDs (LookupAccountSid).</summary>
public interface IAccountLookup
{
    /// <summary>The account of the SID as Winlogon and LogonUser want it (user and domain or PC name); null when Windows does not know it.</summary>
    LogonAccount? Lookup(string sid);

    /// <summary>The SID of an account name (<c>DOMAIN\user</c>, LookupAccountName); null when Windows does not know it.</summary>
    string? SidOf(string account);
}

/// <summary>The Windows sessions that are signed in now (WTSEnumerateSessions).</summary>
public interface ISessions
{
    /// <summary>
    /// The account SIDs of every session with a signed-in user (active or disconnected), except <paramref name="leaving"/> (the session
    /// that is logging off). Null when the sessions cannot be read (the caller then keeps everything as it is).
    /// </summary>
    IReadOnlySet<string>? SignedInSids(int? leaving);
}

/// <summary>Who is signed in to one Windows session (WTSQuerySessionInformation).</summary>
public interface ISessionUsers
{
    /// <summary>The account SID of the user signed in to <paramref name="sessionId"/> now; null when there is none or it cannot be told.</summary>
    string? UserOf(int sessionId);
}

/// <summary>A folder only SYSTEM and administrators may change (the service's state).</summary>
public interface IProtectedFolder
{
    /// <summary>The folder's path, created with its protected access list when missing; null when it cannot be trusted (the reason goes to the log).</summary>
    string? Ensure();
}
