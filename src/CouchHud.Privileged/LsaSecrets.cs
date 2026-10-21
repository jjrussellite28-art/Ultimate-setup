using System.Runtime.InteropServices;

namespace CouchHud.Privileged;

/// <summary>The LSA secret "DefaultPassword" that Winlogon reads for automatic sign-in.</summary>
public sealed class LsaSecrets(IEventSink events) : ILsaSecrets
{
    private const uint PolicyCreateSecret = 0x20;
    private const uint StatusObjectNameNotFound = 0xC0000034;
    private const string SecretName = "DefaultPassword";

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref ObjectAttributes attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaStorePrivateData(IntPtr policy, ref UnicodeString keyName, IntPtr privateData);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    public bool SetDefaultPassword(char[] password) => Store(password);

    public bool DeleteDefaultPassword() => Store(null);

    /// <summary>Stores the secret; null deletes it (a missing secret counts as deleted).</summary>
    private bool Store(char[]? value)
    {
        var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateSecret, out var policy);
        if (status != 0)
        {
            events.Error($"[autologon] LsaOpenPolicy: Windows error {LsaNtStatusToWinError(status)}");
            return false;
        }
        IntPtr name = IntPtr.Zero, data = IntPtr.Zero, dataStruct = IntPtr.Zero;
        var dataBytes = 0;
        try
        {
            name = Marshal.StringToHGlobalUni(SecretName);
            var key = new UnicodeString { Length = (ushort)(SecretName.Length * 2), MaximumLength = (ushort)(SecretName.Length * 2 + 2), Buffer = name };
            if (value is not null)
            {
                dataBytes = value.Length * 2;
                data = Marshal.AllocHGlobal(dataBytes + 2);
                Marshal.Copy(value, 0, data, value.Length);
                Marshal.WriteInt16(data, dataBytes, 0);
                dataStruct = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                Marshal.StructureToPtr(new UnicodeString { Length = (ushort)dataBytes, MaximumLength = (ushort)(dataBytes + 2), Buffer = data }, dataStruct, false);
            }
            status = LsaStorePrivateData(policy, ref key, dataStruct);
            if (status == 0 || (value is null && status == StatusObjectNameNotFound)) return true;
            events.Error($"[autologon] LsaStorePrivateData: Windows error {LsaNtStatusToWinError(status)}");
            return false;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[dataBytes + 2], 0, data, dataBytes + 2);
                Marshal.FreeHGlobal(data);
            }
            if (dataStruct != IntPtr.Zero) Marshal.FreeHGlobal(dataStruct);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
            LsaClose(policy);
        }
    }
}
