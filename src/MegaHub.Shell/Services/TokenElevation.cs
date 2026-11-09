using System.Runtime.InteropServices;

namespace MegaHub.Shell.Services;

/// <summary>
/// The elevation type of this process's token (1 default, 2 full, 3 limited). Limited means an administrator account in Admin Approval Mode:
/// there UAC is only a yes/no click, which matters for the second factor's emergency way (SECURITY.md). Read once; 0 when it cannot be read.
/// </summary>
internal static class TokenElevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationType = 18;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static int? _type;

    public static int Type => _type ??= Read();

    private static int Read()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return 0;
            try
            {
                return GetTokenInformation(token, TokenElevationType, out var type, sizeof(int), out _) ? type : 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }
}
