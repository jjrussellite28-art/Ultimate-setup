using System.Diagnostics;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>The real path behind a companion's program: links, junctions, short names and streams cannot dress a Windows program up as another one.</summary>
public sealed class CompanionPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("couchhud-comp-").FullName;

    public void Dispose()
    {
        try
        {
            // A junction is removed on its own first: deleting the folder must never follow it.
            foreach (var dir in Directory.GetDirectories(_root))
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(dir);
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static LibraryItem App(string path) => new()
    {
        Id = "app",
        Source = "custom",
        Name = "app",
        Type = ItemType.App,
        Launch = new LaunchTarget(LaunchKind.Exe, path)
    };

    private string Make(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z']);
        return path;
    }

    private static bool Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        p!.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    // ---- the spelling of the path

    [Theory]
    [InlineData(@"C:\Apps\wand.exe", true)]
    [InlineData(@"C:/Apps/wand.exe", true)]
    [InlineData(@"C:\Apps\a.txt:x.exe", false)]
    [InlineData(@"C:\Apps\wand.exe:Zone.Identifier", false)]
    [InlineData(@"C:\Apps\w*.exe", false)]
    [InlineData(@"C:\Apps\w?nd.exe", false)]
    [InlineData(@"C:\Apps\..\Windows\cmd.exe", false)]
    [InlineData(@"C:\Apps\.\wand.exe", false)]
    [InlineData(@"C:\Apps\wand.exe.", false)]
    [InlineData(@"C:\Apps\wand.exe ", false)]
    [InlineData(@"C:\Apps\\wand.exe", false)]
    [InlineData(@"\\?\C:\Apps\wand.exe", false)]
    [InlineData(@"\\.\C:\Apps\wand.exe", false)]
    [InlineData(@"\\srv\share\wand.exe", false)]
    [InlineData(@"C:wand.exe", false)]
    [InlineData(@"C:\Apps\wand.com", false)]
    public void Only_a_plain_drive_path_to_an_exe_is_worth_opening(string path, bool plain) =>
        Assert.Equal(plain, CompanionPath.IsPlainProgramPath(path));

    // ---- judged on the real path (pure)

    [Fact]
    public void A_short_spelling_or_a_link_into_the_windows_folder_is_judged_by_where_it_really_is()
    {
        const string win = @"C:\Windows";
        // The library holds an 8.3 spelling of the Windows folder; the real path is in it.
        Assert.Equal(CompanionVerdict.SystemFolder, CompanionPath.Judge(@"C:\WINDOW~1\System32\wand.exe", @"C:\Windows\System32\wand.exe", win));
        // A junction in the user's folder pointing into System32.
        Assert.Equal(CompanionVerdict.SystemFolder, CompanionPath.Judge(@"C:\Users\Anna\tools\wand.exe", @"C:\Windows\System32\wand.exe", win));
        // A link to another program under the asked name: the name was only a mask.
        Assert.Equal(CompanionVerdict.NoProgram, CompanionPath.Judge(@"C:\Apps\wand.exe", @"C:\Apps\cmd.exe", win));
        Assert.Equal(CompanionVerdict.NoProgram, CompanionPath.Judge(@"C:\Apps\wand.exe", @"\\server\share\wand.exe", win));
        // The real path is the one the name rules run on: a shell behind a harmless spelling is a tool.
        Assert.Equal(CompanionVerdict.Tool, CompanionPath.Judge(@"C:\Apps\cmd.exe", @"C:\Tools\cmd.exe", win));
        Assert.Equal(CompanionVerdict.Browser, CompanionPath.Judge(@"C:\Apps\chrome.exe", @"C:\Browsers\chrome.exe", win));
        Assert.Equal(CompanionVerdict.Ok, CompanionPath.Judge(@"C:\APPS\WAND.EXE", @"C:\Apps\Wand.exe", win));
    }

    [Fact]
    public void A_program_under_a_different_name_is_not_recognised()
    {
        // The name filter is a name filter (documented in SECURITY.md): a copy of a shell under a harmless name passes it.
        Assert.Equal(CompanionVerdict.Ok, CompanionPath.Judge(@"C:\Users\Anna\mytool.exe", @"C:\Users\Anna\mytool.exe", @"C:\Windows"));
        Assert.Contains("a program under a different name is not recognised", File.ReadAllText(Path.Combine(Root(), "SECURITY.md")).Replace("\r\n", "\n"));
    }

    // ---- on real files

    [Fact]
    public void A_plain_file_resolves_to_itself_and_a_missing_file_to_nothing()
    {
        var exe = Make(@"apps\wand.exe");
        Assert.Equal(Path.GetFullPath(exe), CompanionPath.Resolve(exe), ignoreCase: true);
        Assert.Null(CompanionPath.Resolve(Path.Combine(_root, "apps", "nothing.exe")));
        // A folder is no program.
        Directory.CreateDirectory(Path.Combine(_root, "apps", "dir.exe"));
        Assert.Null(CompanionPath.Resolve(Path.Combine(_root, "apps", "dir.exe")));
    }

    [Fact]
    public void A_junction_into_the_windows_folder_is_caught_on_the_real_file()
    {
        // The temp folder "sys" stands for the Windows folder; "tools" is a junction to it, so the library's path looks harmless.
        var real = Make(@"sys\System32\wand.exe");
        var link = Path.Combine(_root, "tools");
        // A junction needs no administrator rights, so this has to work on every Windows machine.
        Assert.True(Junction(link, Path.Combine(_root, "sys", "System32")), "mklink /J failed");
        var through = Path.Combine(link, "wand.exe");
        var resolved = CompanionPath.Resolve(through);
        Assert.Equal(Path.GetFullPath(real), resolved, ignoreCase: true);
        var sys = Path.Combine(_root, "sys");
        Assert.Equal(CompanionVerdict.SystemFolder, CompanionRules.Eligible(App(through), new LaunchTarget(LaunchKind.Exe, through), sys));
        // The same spelling is fine when the "Windows folder" is somewhere else.
        Assert.Equal(CompanionVerdict.Ok, CompanionRules.Eligible(App(through), new LaunchTarget(LaunchKind.Exe, through), @"C:\Windows"));
    }

    [Fact]
    public void A_short_name_spelling_of_the_windows_folder_is_expanded()
    {
        var real = Make(@"Program Files Long\Sub Folder\wand.exe");
        var shortDir = ShortPathOf(Path.GetDirectoryName(real)!);
        // 8.3 names are off on some volumes: then there is no alias to test.
        if (shortDir is null || shortDir.Equals(Path.GetDirectoryName(real), StringComparison.OrdinalIgnoreCase)) return;
        var alias = Path.Combine(shortDir, "wand.exe");
        Assert.Equal(Path.GetFullPath(real), CompanionPath.Resolve(alias), ignoreCase: true);
        var win = Path.Combine(_root, "Program Files Long");
        Assert.Equal(CompanionVerdict.SystemFolder, CompanionRules.Eligible(App(alias), new LaunchTarget(LaunchKind.Exe, alias), win));
    }

    [Fact]
    public void A_stream_name_never_opens_a_file()
    {
        var file = Make(@"apps\a.txt");
        var stream = file + ":x.exe";
        Assert.Null(CompanionPath.Resolve(stream));
        Assert.Equal(CompanionVerdict.NoProgram, CompanionRules.Eligible(App(stream), new LaunchTarget(LaunchKind.Exe, stream), @"C:\Windows"));
    }

    [Fact]
    public void A_file_with_a_writer_is_not_started_and_the_held_file_cannot_be_changed()
    {
        var exe = Make(@"apps\held.exe");
        using (var writer = new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Assert.Null(CompanionPath.Hold(exe));
        using var held = CompanionPath.Hold(exe);
        Assert.NotNull(held);
        // While the file is held (between the check and CreateProcess) nobody can write to it or delete it.
        Assert.ThrowsAny<IOException>(() => new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.ThrowsAny<IOException>(() => File.Delete(exe));
        Assert.ThrowsAny<IOException>(() => File.Move(exe, Path.Combine(_root, "apps", "moved.exe")));
        Assert.ThrowsAny<IOException>(() => File.Replace(Make(@"apps\other.exe"), exe, null));
        Assert.True(File.Exists(exe));
        // Reading stays possible (a second program can run it), and nothing holds the file after the hold is released.
        using (var reader = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.Equal(2, reader.Length);
        held.Dispose();
        File.Move(exe, Path.Combine(_root, "apps", "moved.exe"));
        Assert.True(File.Exists(Path.Combine(_root, "apps", "moved.exe")));
    }

    [Fact]
    public void A_symbolic_link_to_another_program_is_a_different_file_name()
    {
        var target = Make(@"apps\realtool.exe");
        var link = Path.Combine(_root, "apps", "wand.exe");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Needs developer mode or an elevated shell: the logic is covered above with the pure judge.
            return;
        }
        Assert.Equal(Path.GetFullPath(target), CompanionPath.Resolve(link), ignoreCase: true);
        Assert.Equal(CompanionVerdict.NoProgram, CompanionRules.Eligible(App(link), new LaunchTarget(LaunchKind.Exe, link), @"C:\Windows"));
    }

    private static string? ShortPathOf(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c for %I in (\"{path}\") do @echo %~sI") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        var text = p!.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text.Length > 0 && Directory.Exists(text) ? text : null;
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
