using System.Runtime.InteropServices;
using CouchHud.Core.Library;

namespace CouchHud.Shell.Services;

/// <summary>
/// The Windows apps folder (shell:AppsFolder): every program the Start menu lists, Store apps (MSIX) included, with the
/// name the shell shows and the id it starts them by. Read on a thread of its own with the apartment the shell wants.
/// </summary>
public static class AppsFolder
{
    private const int NormalDisplay = 0;
    private const int ParentRelativeParsing = unchecked((int)0x80018001);

    private static readonly Guid EnumItemsHandler = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(int form, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems clone);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern IntPtr SHGetKnownFolderPath([In] ref Guid folderId, uint flags, IntPtr token);

    /// <summary>The folder behind a known-folder id ("{7C5A40EF-...}" without braces), null if there is none.</summary>
    public static string? KnownFolder(string guid)
    {
        if (!Guid.TryParse(guid, out var id)) return null;
        try
        {
            var ptr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero);
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Everything in the apps folder, unsorted and uncleaned (<see cref="InstalledApps.Clean"/> does that). Empty when the shell refuses.</summary>
    public static Task<IReadOnlyList<InstalledApp>> ListAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<InstalledApp>>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Enumerate());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[apps] apps folder: {ex.Message}");
                done.SetResult([]);
            }
        })
        { IsBackground = true, Name = "AppsFolder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static IReadOnlyList<InstalledApp> Enumerate()
    {
        var list = new List<InstalledApp>();
        var iid = ShellItemId;
        SHCreateItemFromParsingName(InstalledApps.FolderPrefix.TrimEnd('\\'), IntPtr.Zero, ref iid, out var folder);
        IEnumShellItems? items = null;
        try
        {
            var handler = EnumItemsHandler;
            var enumId = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref handler, ref enumId, out var ptr) != 0 || ptr == IntPtr.Zero) return list;
            items = (IEnumShellItems)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            while (list.Count < 5000 && items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    var name = Display(item, NormalDisplay);
                    var id = Display(item, ParentRelativeParsing);
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) list.Add(new InstalledApp(name, id));
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (items is not null) Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }
        return list;
    }

    private static string? Display(IShellItem item, int form)
    {
        if (item.GetDisplayName(form, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}
