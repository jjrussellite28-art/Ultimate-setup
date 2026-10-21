using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CouchHud.Shell.Services;

/// <summary>
/// Freezes a game while the HUD is in front, like the pause on a console. Windows
/// has no "pause" for programs, so this suspends every thread of the game's processes.
/// Fine for single-player games; online games lose their connection and some
/// anti-cheat systems dislike it, so it is off by default.
/// </summary>
public static class GamePause
{
    private const uint ProcessSuspendResume = 0x0800;

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static readonly HashSet<int> Paused = [];

    /// <summary>
    /// The paused process IDs also go into a file: a suspension outlives CouchHud, so after
    /// a crash the next start (or the guard) can let the game run again.
    /// </summary>
    public static string? StateFile { get; set; }

    public static bool IsPaused => Paused.Count > 0;

    /// <summary>Every process of the game is frozen (one frozen helper does not stop a game).</summary>
    public static bool AllPaused(IEnumerable<int> pids)
    {
        var real = pids.Where(p => p > 0).ToList();
        return real.Count > 0 && real.All(Paused.Contains);
    }

    public static void Pause(IEnumerable<int> pids)
    {
        foreach (var pid in pids.Where(p => p > 0 && !Paused.Contains(p)))
        {
            if (Call(pid, NtSuspendProcess)) Paused.Add(pid);
        }
        Save();
    }

    /// <summary>Lets every paused game run again; safe to call any time.</summary>
    public static void ResumeAll()
    {
        foreach (var pid in Paused.ToList()) Call(pid, NtResumeProcess);
        Paused.Clear();
        Save();
    }

    /// <summary>Resumes what an earlier, crashed CouchHud left frozen.</summary>
    public static void ResumeLeftovers(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            foreach (var line in File.ReadAllLines(file))
            {
                if (int.TryParse(line, out var pid) && pid > 0) Call(pid, NtResumeProcess);
            }
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Save()
    {
        if (StateFile is null) return;
        try
        {
            if (Paused.Count == 0) File.Delete(StateFile);
            else File.WriteAllLines(StateFile, Paused.Select(p => p.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Call(int pid, Func<IntPtr, int> action)
    {
        var handle = OpenProcess(ProcessSuspendResume, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return action(handle) >= 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

/// <summary>
/// The Xbox button opens the Game Bar by default. For console-style "tap to the
/// home screen" CouchHud has to have that button to itself.
/// </summary>
public static class GameBarButton
{
    private const string Key = @"Software\Microsoft\GameBar";
    private const string Value = "UseNexusForGameBarEnabled";

    public static bool OpensGameBar
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Value) is not int v || v != 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(Value, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
