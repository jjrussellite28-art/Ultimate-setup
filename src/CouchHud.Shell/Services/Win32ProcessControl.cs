using System.Diagnostics;
using System.Runtime.InteropServices;
using CouchHud.Core.Guard;
using CouchHud.Shell.Native;

namespace CouchHud.Shell.Services;

/// <summary>
/// Ends programs for <see cref="SafeTermination"/>. The start time is read and compared on the very handle that ends the process,
/// so a process id that was handed out again in the meantime is never hit.
/// </summary>
public sealed class Win32ProcessControl : IProcessControl
{
    public static Win32ProcessControl Instance { get; } = new();

    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimited = 0x1000;

    public int OwnPid => Environment.ProcessId;

    public int OwnSession { get; } = Process.GetCurrentProcess().SessionId;

    public string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<LiveProcess> List()
    {
        var parents = OwnLaunches.ParentMap();
        var buffer = new char[1024];
        var result = new List<LiveProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                int pid;
                string name;
                try
                {
                    pid = p.Id;
                    name = p.ProcessName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    continue;
                }
                int session;
                try { session = p.SessionId; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { session = -1; }
                result.Add(new LiveProcess(pid, parents.TryGetValue(pid, out var parent) ? parent : 0, StartOf(pid), Win32ProcessProbe.ImagePathOf(pid, buffer), session, name));
            }
        }
        return result;
    }

    /// <summary>Creation time of the process (FILETIME ticks); 0 when it cannot be read.</summary>
    public static long StartOf(int pid)
    {
        var handle = NativeMethods.OpenProcess(ProcessQueryLimited, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            return CreationTime(handle);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Creation times of all processes we are allowed to ask.</summary>
    public static Dictionary<int, long> AllStartTimes()
    {
        var map = new Dictionary<int, long>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var t = StartOf(p.Id);
                    if (t > 0) map[p.Id] = t;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return map;
    }

    private static long CreationTime(IntPtr handle) =>
        GetProcessTimes(handle, out var created, out _, out _, out _) ? (long)(((ulong)created.High << 32) | created.Low) : 0;

    public bool RequestClose(int pid, long start)
    {
        if (start <= 0 || StartOf(pid) != start) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public bool Kill(int pid, long start)
    {
        if (start <= 0) return false;
        var handle = NativeMethods.OpenProcess(ProcessTerminate | ProcessQueryLimited, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            // The same handle that reads the start time ends the process: nothing can slip in between.
            if (CreationTime(handle) != start) return false;
            return TerminateProcess(handle, 1);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
