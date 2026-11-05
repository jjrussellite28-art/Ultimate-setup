using System.Text;

namespace CouchHud.Shell.Services;

/// <summary>
/// Builds the small script behind "Reset Mega Hub" and "Uninstall Mega Hub" (Settings › Power). The script runs after Mega Hub has quit,
/// so nothing writes the settings back while they are being deleted. Pure text: nothing here touches the disk or starts a process.
/// </summary>
public static class CleanupScript
{
    private const string AppFolder = "CouchHud";
    private const string UninstallerName = "unins000.exe";
    private const string PublishedFolder = "Mega Hub App";

    /// <summary>A path the script can quote safely: rooted, and none of the characters cmd.exe treats specially inside quotes.</summary>
    public static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && path.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '\r', '\n']) < 0;

    /// <summary>The settings folder is only ever deleted when it is exactly "...\CouchHud" inside the user's AppData.</summary>
    public static bool IsSettingsFolder(string? path, string appData) =>
        IsSafePath(path) && IsSafePath(appData)
        && string.Equals(Path.GetFileName(path!.TrimEnd('\\', '/')), AppFolder, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetDirectoryName(path.TrimEnd('\\', '/')), appData.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The script. Reset: wait for the exit, delete the settings folder, start Mega Hub again. Uninstall: wait for the exit, run the setup's
    /// uninstaller when this copy was installed (it removes the service and everything else that needs administrator rights, with its own
    /// Windows prompt), otherwise run the same cleanup elevated; then delete the settings folder and the desktop shortcut.
    /// </summary>
    public static string Build(bool uninstall, int pid, string exePath, string settingsFolder, string desktopShortcut)
    {
        var dir = Path.GetDirectoryName(exePath) ?? "";
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append("\r\n");
        Line("@echo off");
        Line(uninstall ? "title Uninstall Mega Hub" : "title Reset Mega Hub");
        Line($"set \"EXE={exePath}\"");
        Line($"set \"DIR={dir}\"");
        Line($"set \"DATA={settingsFolder}\"");
        Line($"set \"LINK={desktopShortcut}\"");
        Line("echo Waiting for Mega Hub to close...");
        Line(":wait");
        Line($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        Line("if not errorlevel 1 (");
        Line("  ping -n 2 127.0.0.1 >nul");
        Line("  goto wait");
        Line(")");
        if (uninstall)
        {
            Line("if exist \"%DIR%\\" + UninstallerName + "\" (");
            Line("  echo Starting the uninstaller...");
            Line("  \"%DIR%\\" + UninstallerName + "\"");
            Line(") else (");
            Line("  echo Removing everything Mega Hub changed in Windows. Allow the Windows prompt.");
            Line("  powershell -NoProfile -Command \"Start-Process -FilePath $env:EXE -ArgumentList '--uninstall-cleanup' -Verb RunAs -Wait\"");
            Line(")");
        }
        Line("echo Deleting the settings...");
        Line("if exist \"%DATA%\" rmdir /s /q \"%DATA%\"");
        if (uninstall)
        {
            Line("if exist \"%LINK%\" del /q \"%LINK%\"");
            if (!string.IsNullOrEmpty(dir) && string.Equals(Path.GetFileName(dir.TrimEnd('\\', '/')), PublishedFolder, StringComparison.OrdinalIgnoreCase))
                Line("if exist \"%DIR%\\CouchHud.exe\" rmdir /s /q \"%DIR%\"");
            Line("echo.");
            Line("echo Mega Hub is removed. Your project files and your own folders are not touched.");
            Line("pause");
        }
        else
        {
            Line("echo Starting Mega Hub...");
            Line("start \"\" \"%EXE%\"");
        }
        Line("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }
}
