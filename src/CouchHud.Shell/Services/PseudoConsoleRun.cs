using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CouchHud.Core.Updates;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Shell.Services;

/// <summary>
/// Runs a console program under a pseudo console (ConPTY) and hands its screen output out as text. winget prints its progress bars
/// only to a real console, never into a redirected pipe; under a pseudo console it believes it has one. No window appears (conhost
/// runs headless), nothing is inherited (bInheritHandles false, std handles empty so the pseudo console's own are used), the
/// program is started by its full path with a quoted, fixed argument list, in the system folder.
/// <see cref="TryStartAsync"/> answers null when the pseudo console is not available or does not come up: the caller then falls
/// back to the redirected start.
/// </summary>
internal static class PseudoConsoleRun
{
    public const short Columns = 320;
    public const short Rows = 50;

    /// <summary>The exit code, or null when the pseudo console could not be used (nothing was started).</summary>
    /// <param name="onStarted">Gets the process id once it runs (the elevation watch follows its children).</param>
    /// <exception cref="Win32Exception">The program itself could not be started (not found, no access).</exception>
    /// <exception cref="TimeoutException">It ran longer than <paramref name="timeout"/> and was ended with its children.</exception>
    public static async Task<int?> TryStartAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, Action<ReadOnlyMemory<char>>? onOutput,
        Action<int>? onStarted = null)
    {
        var commandLine = CommandLine.Build(program, args);
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero, console = IntPtr.Zero, attributes = IntPtr.Zero;
        var info = default(ProcessInformation);
        try
        {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0) || !CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) return null;
            int hr;
            try
            {
                hr = CreatePseudoConsole(new Coord { X = Columns, Y = Rows }, inRead, outWrite, 0, out console);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                return null; // Windows before 10 1809
            }
            if (hr != 0)
            {
                Console.Error.WriteLine($"[updates] pseudo console not available (0x{hr:X8})");
                return null;
            }
            // The pseudo console holds its own copies now; ours would keep the pipe from ever ending.
            CloseHandle(inRead);
            inRead = IntPtr.Zero;
            CloseHandle(outWrite);
            outWrite = IntPtr.Zero;

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                Marshal.FreeHGlobal(attributes);
                attributes = IntPtr.Zero;
                return null;
            }
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) return null;

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = StartfUseStdHandles },
                lpAttributeList = attributes
            };
            var line = new StringBuilder(commandLine);
            if (!CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent, IntPtr.Zero, Environment.SystemDirectory, ref startup, out info))
            {
                var error = Marshal.GetLastWin32Error();
                // Not found or not allowed is the program's problem (the redirected start would fail the same way); anything else may be the pseudo console's.
                if (error is 2 or 3 or 5 or 193) throw new Win32Exception(error);
                Console.Error.WriteLine($"[updates] start under the pseudo console failed ({error}), starting redirected");
                return null;
            }
            CloseHandle(info.hThread);
            info.hThread = IntPtr.Zero;
            try { onStarted?.Invoke(info.dwProcessId); } catch (Exception ex) { Console.Error.WriteLine($"[updates] start callback failed: {ex.GetType().Name}"); }

            // The safe handle owns the read end from here on (also when the stream cannot be made), never closed twice.
            var readEnd = new SafeFileHandle(outRead, ownsHandle: true);
            outRead = IntPtr.Zero;
            StreamReader output;
            try
            {
                output = new StreamReader(new FileStream(readEnd, FileAccess.Read, 1), new UTF8Encoding(false));
            }
            catch
            {
                readEnd.Dispose();
                throw;
            }
            using var reader = output;
            // After the run is over nothing is handed out any more, even if the pump still finds a last piece in the pipe.
            var closed = 0;
            Action<ReadOnlyMemory<char>>? forward = onOutput is null ? null : text =>
            {
                if (Volatile.Read(ref closed) == 0) onOutput(text);
            };
            // A real blocking read loop on a thread of its own: the pipe is synchronous and must be drained all the time, or conhost stalls.
            var pumped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pump = pumped.Task;
            new Thread(() =>
            {
                var buffer = new char[1024];
                try
                {
                    int read;
                    while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        try { forward?.Invoke(buffer.AsMemory(0, read)); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine($"[updates] progress callback failed, reading on: {ex.GetType().Name}"); }
                    }
                    try { forward?.Invoke(ReadOnlyMemory<char>.Empty); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine($"[updates] progress callback failed: {ex.GetType().Name}"); }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The pipe ended or the reader was closed.
                }
                finally
                {
                    pumped.TrySetResult();
                }
            }) { IsBackground = true, Name = "pseudo console output" }.Start();

            try
            {
                if (!await WaitAsync(info.hProcess, timeout).ConfigureAwait(false))
                {
                    // Our handle keeps the process id from being reused until we close it, so this can only hit this process (and its tree).
                    try
                    {
                        using var process = Process.GetProcessById(info.dwProcessId);
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                    {
                        TerminateProcess(info.hProcess, 1);
                    }
                    throw new TimeoutException();
                }
            }
            finally
            {
                // Closing the pseudo console ends conhost and with it the output pipe; older Windows block in this call until the
                // pipe is drained, so it runs aside while the pump keeps reading.
                var closing = console;
                console = IntPtr.Zero;
                var close = Task.Run(() => ClosePseudoConsole(closing));
                // The pump is awaited (with a cap) before the reader is disposed; what it still finds after that is not handed out.
                using var cap = new CancellationTokenSource();
                var wait = Task.Delay(TimeSpan.FromSeconds(5), cap.Token);
                await Task.WhenAny(Task.WhenAll(close, pump), wait).ConfigureAwait(false);
                cap.Cancel();
                Volatile.Write(ref closed, 1);
            }
            return GetExitCodeProcess(info.hProcess, out var exit) ? (int)exit : -1;
        }
        finally
        {
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            foreach (var h in new[] { inRead, inWrite, outRead, outWrite, info.hThread, info.hProcess })
            {
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }
    }

    /// <summary>Waits for the process handle to be signalled; false when the time ran out first.</summary>
    private static async Task<bool> WaitAsync(IntPtr process, TimeSpan timeout)
    {
        using var handle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, timedOut) => done.TrySetResult(!timedOut), null, timeout, executeOnlyOnce: true);
        try
        {
            return await done.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    /// <summary>
    /// True when the file is an App Execution Alias (reparse tag IO_REPARSE_TAG_APPEXECLINK), the kind of entry Windows puts into
    /// %LOCALAPPDATA%\Microsoft\WindowsApps for packaged programs, as opposed to a plain file someone copied there.
    /// </summary>
    public static bool IsAppExecutionAlias(string path)
    {
        var handle = FindFirstFileExW(path, 1 /* FindExInfoBasic */, out var data, 0, IntPtr.Zero, 0);
        if (handle == new IntPtr(-1)) return false;
        FindClose(handle);
        return (data.dwFileAttributes & FileAttributeReparsePoint) != 0 && data.dwReserved0 == IoReparseTagAppExecLink;
    }

    private const uint FileAttributeReparsePoint = 0x400;
    private const uint IoReparseTagAppExecLink = 0x8000001B;
    private const int StartfUseStdHandles = 0x100;
    private const int ExtendedStartupInfoPresent = 0x80000;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        // Three FILETIMEs as pairs of DWORDs (a long would be aligned to 8 and shift every field after it).
        public uint ftCreationLow, ftCreationHigh, ftLastAccessLow, ftLastAccessHigh, ftLastWriteLow, ftLastWriteHigh;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, int creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileExW(string fileName, int infoLevel, out Win32FindData data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
