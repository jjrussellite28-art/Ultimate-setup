using System.Diagnostics;
using System.Runtime.InteropServices;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Shell.Services;

public static class Launcher
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;

    /// <summary>Starts a library entry. Store games go through their launcher's URL scheme.</summary>
    /// <returns>Process id of what was started, 0 when shell execute handed over without one.</returns>
    public static int Launch(LibraryItem item)
    {
        // Every part of the start is its own field (program, arguments, folder): nothing is parsed by a shell.
        var plan = LaunchOptions.Plan(item.Launch);
        // A Store (MSIX) app has no exe to run: Windows' activation manager starts it and tells us the process.
        if (plan.PackagedAppId is { } appId && Activate(appId) is { } activated)
        {
            RegisterOwn(activated);
            return activated;
        }
        // Shell execute so executables that need elevation get their UAC prompt.
        var psi = new ProcessStartInfo(plan.FileName)
        {
            UseShellExecute = true,
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory
        };
        // What CouchHud starts for the user is not a competitor: the foreground guard leaves it alone.
        var started = Process.Start(psi);
        OwnLaunches.Register(started);
        var pid = started?.Id ?? 0;
        // Let exactly this process take the foreground; never everyone (ASFW_ANY).
        if (pid != 0) Native.NativeMethods.AllowSetForegroundWindow(pid);
        started?.Dispose();
        return pid;
    }

    /// <summary>The activation manager's start; null when it refuses (then the shell is asked the old way).</summary>
    private static int? Activate(string appUserModelId)
    {
        IApplicationActivationManager? manager = null;
        try
        {
            manager = (IApplicationActivationManager)new ApplicationActivationManager();
            var hr = manager.ActivateApplication(appUserModelId, null, 0, out var pid);
            if (hr >= 0) return (int)pid;
            Console.Error.WriteLine($"[launch] activation of {appUserModelId} failed: 0x{hr:X8}, trying the shell");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Console.Error.WriteLine($"[launch] activation of {appUserModelId}: {ex.Message}, trying the shell");
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
        return null;
    }

    private static void RegisterOwn(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            OwnLaunches.Register(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Gone already: nothing to register.
        }
        Native.NativeMethods.AllowSetForegroundWindow(pid);
    }
}
