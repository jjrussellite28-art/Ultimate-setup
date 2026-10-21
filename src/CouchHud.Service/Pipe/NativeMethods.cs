using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Service.Pipe;

internal static class NativeMethods
{
    public const uint PIPE_ACCESS_DUPLEX = 0x3;
    public const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint PIPE_TYPE_MESSAGE = 0x4;
    public const uint PIPE_READMODE_MESSAGE = 0x2;
    public const uint PIPE_WAIT = 0x0;
    public const uint PIPE_REJECT_REMOTE_CLIENTS = 0x8;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_PIPE_BUSY = 231;
    public const int ERROR_CANT_OPEN_ANONYMOUS = 1347;

    public const uint TOKEN_QUERY = 0x0008;

    public const int TokenUser = 1;
    public const int TokenGroups = 2;
    public const int TokenStatistics = 10;
    public const int TokenRestrictedSids = 11;
    public const int TokenSessionId = 12;
    public const int TokenIntegrityLevel = 25;
    public const int TokenIsAppContainer = 29;

    public const uint SE_GROUP_ENABLED = 0x4;
    public const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, ref SECURITY_ATTRIBUTES securityAttributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenThreadToken(IntPtr thread, uint access, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr buffer, int length, out int returned);
}
