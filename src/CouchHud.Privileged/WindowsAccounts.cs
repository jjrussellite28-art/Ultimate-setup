using System.Runtime.InteropServices;
using System.Security.Principal;
using CouchHud.Core.Guard;
using CouchHud.Core.Security;

namespace CouchHud.Privileged;

/// <summary>LookupAccountSid through <see cref="SecurityIdentifier.Translate"/>: the name Windows reports for the account (local alias and PC name for a Microsoft account, like Sysinternals Autologon).</summary>
public sealed class WindowsAccounts : IAccountLookup
{
    public LogonAccount? Lookup(string sid)
    {
        if (!PolicyGroups.IsUserSid(sid)) return null;
        try
        {
            var name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            var account = AutoLogonAccount.Parse(name, Environment.MachineName);
            return account.User.Length > 0 ? account : null;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    public string? SidOf(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 300) return null;
        try
        {
            var sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
            return PolicyGroups.IsUserSid(sid) ? sid : null;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }
}

/// <summary>The signed-in sessions through the WTS API (no SeTcb needed: only the user and domain names are read).</summary>
public sealed class WindowsSessions(IAccountLookup accounts) : ISessions, ISessionUsers
{
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    public IReadOnlySet<string>? SignedInSids(int? leaving)
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count)) return null;
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(list + i * size);
                if (session.SessionId == 0 || session.SessionId == leaving) continue;
                var user = Query(session.SessionId, WtsUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var domain = Query(session.SessionId, WtsDomainName);
                // A session whose account cannot be resolved: unknown, so nothing may be dropped on its account's behalf.
                if (accounts.SidOf(string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}") is not { } sid) return null;
                sids.Add(sid);
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }
        return sids;
    }

    private const int WtsSessionInfoEx = 25;
    private const int WtsSessionStateLock = 0;
    private const int WtsSessionStateUnlock = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoExLevel1Head
    {
        public int Level;
        // WTSINFOEX_LEVEL1_W holds LARGE_INTEGER times: the union after Level is 8-byte aligned.
        public int Padding;
        public int SessionId;
        public int SessionState;
        public int SessionFlags;
    }

    /// <summary>
    /// Whether the session is locked right now (WTSQuerySessionInformation WTSSessionInfoEx, SessionFlags; Windows 10 and later report
    /// WTS_SESSIONSTATE_LOCK = 0 and WTS_SESSIONSTATE_UNLOCK = 1). Null when Windows cannot tell (the caller then refuses).
    /// </summary>
    public bool? IsLocked(int sessionId)
    {
        if (sessionId <= 0 || !WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsSessionInfoEx, out var buffer, out var bytes)) return null;
        try
        {
            if (bytes < Marshal.SizeOf<InfoExLevel1Head>()) return null;
            var info = Marshal.PtrToStructure<InfoExLevel1Head>(buffer);
            if (info.Level != 1) return null;
            return info.SessionFlags switch
            {
                WtsSessionStateLock => true,
                WtsSessionStateUnlock => false,
                _ => null
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public string? UserOf(int sessionId)
    {
        if (sessionId <= 0) return null;
        var user = Query(sessionId, WtsUserName);
        if (string.IsNullOrEmpty(user)) return null;
        var domain = Query(sessionId, WtsDomainName);
        return accounts.SidOf(string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}");
    }

    private static string? Query(int session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}
