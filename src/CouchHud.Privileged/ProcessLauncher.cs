using System.Diagnostics;

namespace CouchHud.Privileged;

/// <summary>
/// Starts programs by their full path only (a bare name would be looked up in the current directory and on the PATH, which the
/// caller of a privileged process does not control well). The time limit is real: the output is read in the background, and a
/// program that is still running when the time is up is killed with its children.
/// </summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    /// <summary>How long to wait for the output streams after the process ended (a child may still hold them open).</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);

    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        RequireFullPath(fileName);
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(info);
            if (p is null) return new ProcessResult(-1, "", "");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            if (!p.WaitForExit(milliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                p.WaitForExit(2000);
                return new ProcessResult(-1, "", "");
            }
            Task.WaitAll([output, error], DrainLimit);
            return new ProcessResult(p.ExitCode, output.IsCompletedSuccessfully ? output.Result : "", error.IsCompletedSuccessfully ? error.Result : "");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, "", "");
        }
    }

    public int? StartDetached(string fileName, IReadOnlyList<string> arguments)
    {
        RequireFullPath(fileName);
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(info);
            return p?.Id;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Drive and folder, or a UNC path: not "tool.exe", not "\tool.exe", not "C:tool.exe".</summary>
    internal static void RequireFullPath(string fileName)
    {
        if (!Path.IsPathFullyQualified(fileName)) throw new ArgumentException("the program must be given by its full path", nameof(fileName));
    }
}
