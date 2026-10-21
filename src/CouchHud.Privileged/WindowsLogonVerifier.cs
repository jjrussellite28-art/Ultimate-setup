using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CouchHud.Privileged;

/// <summary>LogonUser (interactive) to check a password; the result names the SID of the token.</summary>
public sealed class WindowsLogonVerifier : ILogonVerifier
{
    private const int LogonInteractive = 2;
    private const int ProviderDefault = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string user, string? domain, string password, int type, int provider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public LogonResult Verify(string user, string? domain, string password)
    {
        if (!LogonUser(user, domain, password, LogonInteractive, ProviderDefault, out var token)) return new LogonResult(Marshal.GetLastWin32Error(), null);
        try
        {
            using var identity = new WindowsIdentity(token);
            return new LogonResult(0, identity.User?.Value);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
