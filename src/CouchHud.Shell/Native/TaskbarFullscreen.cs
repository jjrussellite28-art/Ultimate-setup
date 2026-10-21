using System.Runtime.InteropServices;
using CouchHud.Shell.Services;

namespace CouchHud.Shell.Native;

/// <summary>
/// Tells the taskbar that a window is a full-screen one (ITaskbarList2.MarkFullscreenWindow), the way games
/// and video players do. Then the taskbar stays below the HUD even when Windows refuses us the foreground.
/// </summary>
internal static class TaskbarFullscreen
{
    public static void Mark(IntPtr hwnd, bool fullscreen)
    {
        if (hwnd == IntPtr.Zero) return;
        // Without a taskbar (explorer not running, the usual case as the shell) there is nothing to tell:
        // its COM object answers E_NOTIMPL there, which surfaces as NotImplementedException.
        if (!ShellManager.IsDesktopShellRunning()) return;
        try
        {
            var list = (ITaskbarList2)new TaskbarListClass();
            try
            {
                list.HrInit();
                list.MarkFullscreenWindow(hwnd, fullscreen);
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cosmetic only: never let it stop the HUD from coming up.
            Console.Error.WriteLine($"[window] taskbar fullscreen mark failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarListClass;

    [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }
}
