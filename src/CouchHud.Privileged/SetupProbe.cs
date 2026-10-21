using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CouchHud.Core.Localization;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Privileged;

/// <summary>
/// Windows calls around running a downloaded setup: where the file really is (asked of a handle that is held, so a link or a
/// swapped path changes nothing) and which image a process really runs.
/// </summary>
public static class SetupProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    /// <summary>The real path of an open file (links resolved), as "\\?\C:\…".</summary>
    public static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException(Strings.Get("setup.pathUnknownSetup"));
        return buffer.ToString();
    }

    /// <summary>The executable a process runs, or null if Windows doesn't tell (an elevated process may refuse).</summary>
    public static string? ImageOf(Process process)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process.SafeHandle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                // Same name as the setup we started; a recycled id of another program doesn't count.
                return !process.HasExited && process.ProcessName.StartsWith("CouchHud-Setup", StringComparison.OrdinalIgnoreCase);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return true;   // exists, only not askable
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
