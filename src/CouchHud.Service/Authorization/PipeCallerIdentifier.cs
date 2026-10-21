using System.Runtime.InteropServices;
using System.Security.Principal;
using CouchHud.Service.Logging;
using CouchHud.Service.Pipe;
using Microsoft.Win32.SafeHandles;
using static CouchHud.Service.Pipe.NativeMethods;

namespace CouchHud.Service.Authorization;

/// <summary>Reads who is on the other end of a connected pipe. Null means: unknown, deny.</summary>
public interface ICallerIdentifier
{
    CallerInfo? Identify(SafePipeHandle pipe);
}

/// <summary>
/// The thin Win32 adapter: process and session from the pipe, everything else (user, groups, integrity, logon LUID, restricting SIDs) from the caller's token (impersonate, open the thread
/// token, revert; nothing from the request). Needs only Identification level from the client (TOKEN_QUERY on the thread token).
/// Any failure gives null (fail-closed).
/// </summary>
public sealed class PipeCallerIdentifier(DenyLog log) : ICallerIdentifier
{
    private const string Interactive = "S-1-5-4";
    private const string Network = "S-1-5-2";
    private const string Batch = "S-1-5-3";
    private const string ServiceLogon = "S-1-5-6";
    private const string Anonymous = "S-1-5-7";
    private const string Administrators = "S-1-5-32-544";

    public CallerInfo? Identify(SafePipeHandle pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe, out var pid) || pid == 0) return Fail("client process id");
            if (!GetNamedPipeClientSessionId(pipe, out var pipeSession)) return Fail("client session id");
            using var token = OpenClientToken(pipe);
            if (token is null) return Fail("client token");

            var user = ReadSid(token, TokenUser);
            var groups = ReadGroups(token);
            bool Has(string sid) => groups.Any(g => g.Sid == sid);
            bool Live(string sid) => groups.Any(g => g.Sid == sid && (g.Attributes & SE_GROUP_ENABLED) != 0 && (g.Attributes & SE_GROUP_USE_FOR_DENY_ONLY) == 0);
            return new CallerInfo(
                Sid: user,
                LogonId: ReadLogonId(token),
                ProcessId: checked((int)pid),
                PipeSessionId: checked((int)pipeSession),
                TokenSessionId: ReadInt(token, TokenSessionId),
                IntegrityLevel: ReadIntegrity(token),
                IsInteractive: Live(Interactive),
                IsNetwork: Has(Network),
                IsService: Has(ServiceLogon) || Has(Batch),
                IsAnonymous: user == Anonymous || Has(Anonymous),
                IsAppContainer: ReadInt(token, TokenIsAppContainer) != 0,
                IsRestricted: HasRestrictingSids(token),
                IsAdminGroupEnabled: Live(Administrators));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error(null, $"[caller] identification failed: {ex.GetType().Name}");
            return null;
        }
    }

    private CallerInfo? Fail(string what)
    {
        log.Error(null, $"[caller] {what} not readable (Win32 {Marshal.GetLastPInvokeError()})");
        return null;
    }

    /// <summary>
    /// The client's token. Impersonation belongs to the thread: this method is synchronous, nothing awaits in between, and the
    /// thread is reverted in finally. If reverting fails, the thread would go on as the client: the process ends instead.
    /// </summary>
    private static SafeAccessTokenHandle? OpenClientToken(SafePipeHandle pipe)
    {
        if (!ImpersonateNamedPipeClient(pipe)) return null;
        SafeAccessTokenHandle? token = null;
        try
        {
            // openAsSelf: the access check uses the service's own identity, not the client's.
            if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, out token))
            {
                token.Dispose();
                return null;
            }
            return token;
        }
        finally
        {
            if (!RevertToSelf()) Environment.FailFast("RevertToSelf failed after reading the pipe client's token");
        }
    }

    private static T WithInfo<T>(SafeAccessTokenHandle token, int infoClass, Func<IntPtr, int, T> read)
    {
        GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20) throw new InvalidOperationException($"token information {infoClass}");
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, needed, out _)) throw new InvalidOperationException($"token information {infoClass}");
            return read(buffer, needed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // TOKEN_USER and TOKEN_MANDATORY_LABEL both start with SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; }.
    private static string ReadSid(SafeAccessTokenHandle token, int infoClass) =>
        WithInfo(token, infoClass, (buffer, _) => new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value);

    private static int ReadIntegrity(SafeAccessTokenHandle token)
    {
        var label = ReadSid(token, TokenIntegrityLevel);   // S-1-16-<rid>
        if (!label.StartsWith("S-1-16-", StringComparison.Ordinal) || !int.TryParse(label.AsSpan(7), out var rid)) throw new InvalidOperationException("integrity label");
        return rid;
    }

    private static int ReadInt(SafeAccessTokenHandle token, int infoClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, sizeof(int), out _)) throw new InvalidOperationException($"token information {infoClass}");
            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // TOKEN_GROUPS { DWORD GroupCount; SID_AND_ATTRIBUTES Groups[]; } — the array is pointer-aligned.
    private static List<(string Sid, uint Attributes)> ReadGroups(SafeAccessTokenHandle token) =>
        WithInfo(token, TokenGroups, (buffer, size) =>
        {
            var count = Marshal.ReadInt32(buffer);
            var entrySize = IntPtr.Size * 2;
            if (count < 0 || IntPtr.Size + (long)count * entrySize > size) throw new InvalidOperationException("token groups");
            var groups = new List<(string, uint)>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = buffer + IntPtr.Size + i * entrySize;
                groups.Add((new SecurityIdentifier(Marshal.ReadIntPtr(entry)).Value, (uint)Marshal.ReadInt32(entry + IntPtr.Size)));
            }
            return groups;
        });

    /// <summary>A sandbox token carries restricting SIDs; one that has any, or whose list cannot be read, counts as restricted.</summary>
    private static bool HasRestrictingSids(SafeAccessTokenHandle token)
    {
        try
        {
            return WithInfo(token, TokenRestrictedSids, (buffer, _) => Marshal.ReadInt32(buffer)) != 0;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    // TOKEN_STATISTICS { LUID TokenId; LUID AuthenticationId; … }: the logon session's LUID at offset 8.
    private static long ReadLogonId(SafeAccessTokenHandle token) =>
        WithInfo(token, TokenStatistics, (buffer, size) => size >= 16 ? Marshal.ReadInt64(buffer, 8) : throw new InvalidOperationException("token statistics"));
}