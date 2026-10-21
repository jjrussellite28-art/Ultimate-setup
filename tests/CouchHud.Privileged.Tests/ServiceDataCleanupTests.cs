using System.Diagnostics;
using System.Security.AccessControl;
using CouchHud.Core.Security;
using CouchHud.Privileged;

namespace CouchHud.Privileged.Tests;

/// <summary>
/// The one-time cleanup of a stale ProgramData\CouchHud\Service: only when protected, everything opened relative to its folder's
/// handle, links removed as links, foreign-owned subfolders left, no attribute changed through a hard link.
/// </summary>
public class ServiceDataCleanupTests
{
    private sealed class Events : IEventSink
    {
        public void Info(string message) { }

        public void Error(string message) { }
    }

    private static readonly ServiceDataCleanup.Rules TrustAll = new(_ => true, _ => true, _ => true);

    private static string NewTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool Mklink(string kind, string link, string target)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var p = Process.Start(new ProcessStartInfo(cmd) { ArgumentList = { "/d", "/c", "mklink", kind, link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit(10_000);
        return p.ExitCode == 0;
    }

    /// <summary>Removes test folders without following the links in them.</summary>
    private static void Cleanup(params string[] roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            Remove(new DirectoryInfo(root));
        }

        static void Remove(DirectoryInfo dir)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { if (entry is DirectoryInfo d) d.Delete(); else entry.Delete(); continue; }
                if (entry is DirectoryInfo sub) { Remove(sub); continue; }
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
            dir.Delete();
        }
    }

    private static string State(string root) => ServiceInstall.LegacyProgramDataFolder(root);

    [Fact]
    public void A_protected_state_folder_and_its_empty_parent_go_read_only_files_included()
    {
        var root = NewTemp();
        try
        {
            Directory.CreateDirectory(Path.Combine(State(root), "staging", "x"));
            File.WriteAllText(Path.Combine(State(root), "staging", "x", "a.bin"), "x");
            File.WriteAllText(Path.Combine(State(root), "state.json"), "x");
            File.SetAttributes(Path.Combine(State(root), "state.json"), FileAttributes.ReadOnly);
            Assert.True(ServiceDataCleanup.Delete(root, new Events(), TrustAll));
            Assert.False(Directory.Exists(Path.Combine(root, "CouchHud")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Nothing_there_is_fine_and_other_content_of_the_parent_stays()
    {
        var root = NewTemp();
        try
        {
            Assert.True(ServiceDataCleanup.Delete(root, new Events(), TrustAll));
            Directory.CreateDirectory(State(root));
            File.WriteAllText(Path.Combine(root, "CouchHud", "other.txt"), "x");
            Assert.True(ServiceDataCleanup.Delete(root, new Events(), TrustAll));
            Assert.False(Directory.Exists(State(root)));
            Assert.True(File.Exists(Path.Combine(root, "CouchHud", "other.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void An_unprotected_parent_or_state_folder_is_left_alone()
    {
        var root = NewTemp();
        try
        {
            Directory.CreateDirectory(State(root));
            File.WriteAllText(Path.Combine(State(root), "keep.txt"), "x");
            Assert.False(ServiceDataCleanup.Delete(root, new Events(), TrustAll with { ParentSafe = _ => false }));
            Assert.False(ServiceDataCleanup.Delete(root, new Events(), TrustAll with { FolderSafe = _ => false }));
            // The real rules: a temp folder belongs to whoever runs the tests and inherits their rights.
            Assert.False(ServiceDataCleanup.Delete(root, new Events()));
            Assert.True(File.Exists(Path.Combine(State(root), "keep.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_subfolder_with_a_foreign_owner_is_left_the_rest_goes()
    {
        var root = NewTemp();
        try
        {
            Directory.CreateDirectory(Path.Combine(State(root), "foreign"));
            File.WriteAllText(Path.Combine(State(root), "foreign", "keep.txt"), "x");
            File.WriteAllText(Path.Combine(State(root), "gone.txt"), "x");
            Assert.False(ServiceDataCleanup.Delete(root, new Events(), TrustAll with { TrustedOwner = _ => false }));
            Assert.True(File.Exists(Path.Combine(State(root), "foreign", "keep.txt")));
            Assert.False(File.Exists(Path.Combine(State(root), "gone.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Links_are_removed_never_followed()
    {
        var root = NewTemp();
        var outside = NewTemp();
        try
        {
            File.WriteAllText(Path.Combine(outside, "victim.txt"), "x");
            Directory.CreateDirectory(State(root));
            Assert.True(Mklink("/J", Path.Combine(State(root), "link"), outside));
            Assert.True(ServiceDataCleanup.Delete(root, new Events(), TrustAll));
            Assert.True(File.Exists(Path.Combine(outside, "victim.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "CouchHud")));

            // The state folder itself a junction: only the link goes.
            Directory.CreateDirectory(Path.Combine(root, "CouchHud"));
            Assert.True(Mklink("/J", State(root), outside));
            ServiceDataCleanup.Delete(root, new Events(), TrustAll);
            Assert.True(File.Exists(Path.Combine(outside, "victim.txt")));
            Assert.False(Directory.Exists(State(root)));

            // The parent a junction: nothing is touched.
            Directory.CreateDirectory(Path.Combine(outside, "Service"));
            File.WriteAllText(Path.Combine(outside, "Service", "victim.txt"), "x");
            if (Directory.Exists(Path.Combine(root, "CouchHud"))) Directory.Delete(Path.Combine(root, "CouchHud"));
            Assert.True(Mklink("/J", Path.Combine(root, "CouchHud"), outside));
            Assert.False(ServiceDataCleanup.Delete(root, new Events(), TrustAll));
            Assert.True(File.Exists(Path.Combine(outside, "Service", "victim.txt")));
        }
        finally
        {
            Cleanup(root, outside);
        }
    }

    [Fact]
    public void A_subfolder_swapped_for_a_junction_after_listing_is_not_entered()
    {
        var root = NewTemp();
        var outside = NewTemp();
        try
        {
            File.WriteAllText(Path.Combine(outside, "victim.txt"), "x");
            Directory.CreateDirectory(Path.Combine(State(root), "sub"));
            File.WriteAllText(Path.Combine(State(root), "sub", "a.txt"), "x");
            var rules = TrustAll with
            {
                BeforeOpen = (_, name) =>
                {
                    if (name != "sub") return;
                    // The race: between the listing and the open, the folder is replaced by a junction.
                    Directory.Delete(Path.Combine(State(root), "sub"), true);
                    Assert.True(Mklink("/J", Path.Combine(State(root), "sub"), outside));
                }
            };
            ServiceDataCleanup.Delete(root, new Events(), rules);
            Assert.True(File.Exists(Path.Combine(outside, "victim.txt")));
            Assert.False(Directory.Exists(Path.Combine(State(root), "sub")));
        }
        finally
        {
            Cleanup(root, outside);
        }
    }

    [Fact]
    public void A_read_only_hard_link_to_a_file_elsewhere_keeps_that_file_unchanged()
    {
        var root = NewTemp();
        var outside = NewTemp();
        try
        {
            var victim = Path.Combine(outside, "victim.txt");
            File.WriteAllText(victim, "keep");
            File.SetAttributes(victim, FileAttributes.ReadOnly);
            Directory.CreateDirectory(State(root));
            Assert.True(Mklink("/H", Path.Combine(State(root), "hard.txt"), victim));
            ServiceDataCleanup.Delete(root, new Events(), TrustAll);
            Assert.False(File.Exists(Path.Combine(State(root), "hard.txt")));
            Assert.Equal("keep", File.ReadAllText(victim));
            Assert.True(File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            Cleanup(root, outside);
        }
    }

    [Theory]
    [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)", true, true)]
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", true, true)]
    [InlineData("O:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", true, false)]   // DACL inherits from above
    [InlineData("O:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", false, true)]   // ... which the parent rule does not ask for
    [InlineData("O:BUD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", true, false)]  // owned by a user
    [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BU)", true, false)]  // users full
    [InlineData("O:SYD:P(A;OICI;GW;;;SY)(A;OICI;GW;;;AU)", true, false)]  // generic write for authenticated users
    [InlineData("O:SYD:P(A;OICI;0x40;;;BU)", true, false)]                // delete child
    [InlineData("O:SYD:P(A;OICI;0x10000;;;BU)", true, false)]             // delete
    [InlineData("O:SYD:P(A;OICI;WD;;;BU)", true, false)]                  // change permissions
    public void The_state_folder_counts_as_protected_only_with_a_trusted_owner_and_no_foreign_writer(string sddl, bool requireProtectedDacl, bool expected)
    {
        Assert.Equal(expected, ServiceDataCleanup.IsProtected(new RawSecurityDescriptor(sddl), FolderProtection.WriteMask, requireProtectedDacl));
    }

    [Fact]
    public void A_null_dacl_is_never_protected()
    {
        Assert.False(ServiceDataCleanup.IsProtected(new RawSecurityDescriptor("O:SYD:NO_ACCESS_CONTROL"), FolderProtection.WriteMask, false));
    }
}
