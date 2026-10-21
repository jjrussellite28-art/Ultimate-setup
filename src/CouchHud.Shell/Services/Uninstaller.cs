using System.Diagnostics;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Sdk;
using Microsoft.Win32;

namespace CouchHud.Shell.Services;

/// <summary>Finds and starts the uninstaller for a library entry.</summary>
public static class Uninstaller
{
    private static readonly (RegistryKey Hive, string Path)[] UninstallKeys =
    [
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
    ];

    /// <summary>Command line or URL that uninstalls the item, or null if none is known.</summary>
    public static string? Find(LibraryItem item)
    {
        // Steam handles its own games (and asks for confirmation itself).
        if (SteamStore.AppIdOf(item) is { } appId) return $"steam://uninstall/{appId}";

        var installDir = Normalize(item.InstallDir);
        foreach (var (hive, path) in UninstallKeys)
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("UninstallString") is not string command || string.IsNullOrWhiteSpace(command)) continue;

                var location = Normalize(key.GetValue("InstallLocation") as string);
                var displayName = key.GetValue("DisplayName") as string;
                var sameFolder = installDir is not null && location is not null
                                 && string.Equals(location, installDir, StringComparison.OrdinalIgnoreCase);
                var sameName = displayName is not null
                               && string.Equals(displayName.Trim(), item.Name.Trim(), StringComparison.OrdinalIgnoreCase);
                if (sameFolder || sameName) return command;
            }
        }
        return null;
    }

    public static void Run(string command)
    {
        if (command.Contains("://", StringComparison.Ordinal) && !command.StartsWith('"'))
        {
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true })?.Dispose();
            return;
        }
        // Shell execute, so uninstallers that need admin rights get their UAC prompt.
        var (file, args) = CommandLine.Split(command);
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose();
    }

    private static string? Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"').TrimEnd('\\', '/');
}
