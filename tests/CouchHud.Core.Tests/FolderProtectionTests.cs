using CouchHud.Core.Security;
using CouchHud.Core.Startup;

namespace CouchHud.Core.Tests;

public class FolderProtectionTests
{
    private const string System = "S-1-5-18", Admins = "S-1-5-32-544", Users = "S-1-5-32-545", AuthUsers = "S-1-5-11", Everyone = "S-1-1-0";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const int Full = 0x1F01FF, ReadExecute = 0x1200A9, Modify = 0x1301BF;

    [Fact]
    public void Program_Files_like_acl_is_protected()
    {
        // TrustedInstaller owner; admins and system full; users read and execute; "Creator Owner" full for children only.
        var entries = new AclEntry[]
        {
            new(TrustedInstaller, Full), new(System, Full), new(Admins, Full), new(Users, ReadExecute), new("S-1-3-0", Full, AppliesHere: false)
        };
        Assert.True(FolderProtection.IsProtected(TrustedInstaller, entries));
    }

    [Theory]
    [InlineData(Users)]
    [InlineData(AuthUsers)]
    [InlineData(Everyone)]
    [InlineData("S-1-5-21-111-222-333-1001")] // the current user
    public void Write_for_users_or_the_current_user_is_not_protected(string sid)
    {
        var entries = new AclEntry[] { new(Admins, Full), new(System, Full), new(sid, Modify) };
        Assert.False(FolderProtection.IsProtected(Admins, entries));
    }

    [Fact]
    public void Every_kind_of_write_counts()
    {
        foreach (var right in new[] { FolderProtection.WriteData, FolderProtection.AppendData, FolderProtection.DeleteChild, FolderProtection.Delete, FolderProtection.ChangePermissions, FolderProtection.TakeOwnership })
        {
            Assert.False(FolderProtection.IsProtected(Admins, [new(Admins, Full), new(Users, right)]), $"right {right:X}");
        }
    }

    [Fact]
    public void An_entry_for_children_only_does_not_make_the_folder_writable()
    {
        Assert.True(FolderProtection.IsProtected(Admins, [new(Admins, Full), new(AuthUsers, Modify, AppliesHere: false)]));
    }

    [Fact]
    public void A_user_as_owner_can_change_the_acl_and_is_not_protected()
    {
        Assert.False(FolderProtection.IsProtected("S-1-5-21-111-222-333-1001", [new(Admins, Full)]));
        Assert.False(FolderProtection.IsProtected(null, [new(Admins, Full)]));
    }

    [Fact]
    public void A_folder_above_may_be_appended_to_but_not_replaced()
    {
        // C:\ lets users create folders; that does not let them swap an existing one.
        var root = new AclEntry[] { new(Admins, Full), new(System, Full), new(Users, FolderProtection.AppendData), new(Users, ReadExecute) };
        Assert.True(FolderProtection.IsProtected(System, root, FolderProtection.ReplaceMask));
        Assert.False(FolderProtection.IsProtected(System, root, FolderProtection.WriteMask));
    }

    [Fact]
    public void Setup_exit_codes_have_their_own_message_and_none_says_declined()
    {
        Assert.Equal("services.needProtected", SetupExit.MessageKey(SetupExit.UnsafeFolder));
        Assert.Equal("services.setupBadInput", SetupExit.MessageKey(SetupExit.BadInput));
        Assert.Equal("services.setupTaskFailed", SetupExit.MessageKey(SetupExit.TaskFailed));
        Assert.Equal("services.setupFailed", SetupExit.MessageKey(99));
        foreach (var code in new[] { 2, 3, 4, 99 })
        {
            Assert.NotEqual("admin.declined", SetupExit.MessageKey(code));
            // The texts exist in both languages.
            Assert.True(CouchHud.Core.Localization.Strings.German.ContainsKey(SetupExit.MessageKey(code)));
            Assert.True(CouchHud.Core.Localization.Strings.English.ContainsKey(SetupExit.MessageKey(code)));
        }
    }
}
