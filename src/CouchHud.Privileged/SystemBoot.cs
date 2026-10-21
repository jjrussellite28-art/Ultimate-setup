using System.Runtime.InteropServices;

namespace CouchHud.Privileged;

/// <summary>
/// The machine's boot time as the kernel keeps it (NtQuerySystemInformation, SystemTimeOfDayInformation.BootTime): it changes only
/// with a restart, unlike "now minus the tick count", which moves with every change of the clock.
/// </summary>
public static class SystemBoot
{
    private const int SystemTimeOfDayInformation = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeOfDay
    {
        public long BootTime;
        public long CurrentTime;
        public long TimeZoneBias;
        public uint TimeZoneId;
        public uint Reserved;
        public ulong BootTimeBias;
        public ulong SleepTimeBias;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, out TimeOfDay information, int length, out int returnLength);

    /// <summary>The boot time in Unix milliseconds, null when the kernel did not tell.</summary>
    public static long? TimeMs()
    {
        try
        {
            var status = NtQuerySystemInformation(SystemTimeOfDayInformation, out var info, Marshal.SizeOf<TimeOfDay>(), out _);
            if (status != 0 || info.BootTime <= 0) return null;
            return DateTimeOffset.FromFileTime(info.BootTime).ToUnixTimeMilliseconds();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
