using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using CouchHud.Core.Remote;

namespace CouchHud.Shell.Services;

/// <summary>
/// Looks at the field that has the keyboard focus in another program, before the phone types into it:
/// a Win32 edit control with ES_PASSWORD, a UI Automation element that says IsPassword, and fields whose
/// name reads like a secret. Runs off the UI thread (UI Automation can wait on a program that hangs).
/// </summary>
public static class FocusProbe
{
    private const int GwlStyle = -16;
    private const int EsPassword = 0x20;

    /// <summary>What was looked at: the field, the window in front at that moment, and whose it is.</summary>
    public sealed record Snapshot(FocusedField Field, IntPtr Window, string? ProcessName, bool OwnProcess);

    private static readonly object Gate = new();
    private static Task<Snapshot>? _inFlight;

    /// <summary>
    /// One look at a time. UI Automation can wait on a program that hangs for a long time; if the last look has not come back yet,
    /// the next one is not started (no thread per request piling up behind a hung program) and the answer is null: not checked.
    /// Null as well when the look takes longer than <paramref name="timeout"/>.
    /// </summary>
    public static async Task<Snapshot?> ProbeAsync(TimeSpan timeout)
    {
        Task<Snapshot> task;
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false }) return null;
            task = _inFlight = Task.Run(Look);
        }
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Is this still the window in front? Checked right before typing: the field was looked at a moment ago.</summary>
    public static bool StillForeground(IntPtr window) => window != IntPtr.Zero && GetForegroundWindow() == window;

    private static Snapshot Look()
    {
        var window = GetForegroundWindow();
        string? process = null;
        var own = false;
        try
        {
            if (window != IntPtr.Zero)
            {
                GetWindowThreadProcessId(window, out var pid);
                own = pid == Environment.ProcessId;
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                process = p.ProcessName;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already: the name stays unknown.
        }
        return new Snapshot(Read(), window, process, own);
    }

    public static FocusedField Read()
    {
        bool? flag = null;
        var labels = new List<string?>();

        try
        {
            var win32 = Win32Focus();
            if (win32 is { } style && IsEditClass(style.Class)) flag = (style.Style & EsPassword) != 0 ? true : false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[remote] focus probe (win32): {ex.Message}");
        }

        try
        {
            if (AutomationElement.FocusedElement is { } element)
            {
                var info = element.Current;
                if (info.IsPassword) flag = true;
                // "Not a password" is only known of an element that is an input field: the window of a browser or a launcher
                // (Chromium without accessibility yet) answers IsPassword = false for the whole page.
                else if (info.ControlType == ControlType.Edit || info.ControlType == ControlType.ComboBox) flag ??= false;
                labels.Add(info.Name);
                labels.Add(info.AutomationId);
                labels.Add(info.HelpText);
                labels.Add(info.LabeledBy?.Current.Name);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.ComponentModel.Win32Exception or COMException or TimeoutException)
        {
            Console.Error.WriteLine($"[remote] focus probe (automation): {ex.Message}");
        }
        return new FocusedField(flag, labels);
    }

    private static bool IsEditClass(string name) =>
        name.Equals("Edit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TEdit", StringComparison.OrdinalIgnoreCase);

    private static (string Class, int Style)? Win32Focus()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;
        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero) return null;
        var name = new StringBuilder(64);
        GetClassName(info.Focus, name, name.Capacity);
        return (name.ToString(), GetWindowLong(info.Focus, GwlStyle));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);
}
