using CouchHud.Core.Localization;
namespace CouchHud.Core.Files;

public enum EntryKind
{
    Drive,
    Place,
    Folder,
    File
}

public sealed record BrowseEntry(string Name, string Path, EntryKind Kind, long Size = 0);

public sealed record BrowseResult(string? Path, string? Parent, IReadOnlyList<BrowseEntry> Entries);

/// <summary>Directory listings for the controller file browser.</summary>
public static class FileBrowser
{
    /// <summary>
    /// Lists a folder: subfolders first, then files (optionally only certain extensions).
    /// Without a path it returns drives and common places. Hidden and system entries are skipped.
    /// </summary>
    public static BrowseResult List(string? path, IReadOnlyCollection<string>? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return Roots();

        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException(Strings.Get("files.folderNotFound", path));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        var folders = dir.EnumerateDirectories("*", options)
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new BrowseEntry(d.Name, d.FullName, EntryKind.Folder));
        var files = dir.EnumerateFiles("*", options)
            .Where(f => extensions is null || extensions.Count == 0 || extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new BrowseEntry(f.Name, f.FullName, EntryKind.File, f.Length));

        return new BrowseResult(dir.FullName, dir.Parent?.FullName, [.. folders, .. files]);
    }

    private static BrowseResult Roots()
    {
        var entries = new List<BrowseEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\', '/')})";
                entries.Add(new BrowseEntry(label, drive.RootDirectory.FullName, EntryKind.Drive, drive.TotalFreeSpace));
            }
            catch (IOException)
            {
                // Card readers and disconnected network drives.
            }
        }

        void Place(string name, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) entries.Add(new BrowseEntry(name, path, EntryKind.Place));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Place(Strings.Get("files.place.desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Place(Strings.Get("files.place.downloads"), string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads"));
        Place(Strings.Get("files.place.pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Place(Strings.Get("files.place.programs"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Place(Strings.Get("files.place.programsX86"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        return new BrowseResult(null, null, entries);
    }
}
