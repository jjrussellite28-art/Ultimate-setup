using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

public class WinlogonNamesTests
{
    [Fact]
    public void The_previous_name_comes_back_while_ours_is_still_there()
    {
        Assert.Equal(new WinlogonRestore(WinlogonRestoreKind.Write, "Admin"), WinlogonNames.Restore("player", "player", "Admin", previousSaved: true));
        Assert.Equal(new WinlogonRestore(WinlogonRestoreKind.Write, "Admin"), WinlogonNames.Restore("PLAYER", "player", "Admin", previousSaved: true));
    }

    [Fact]
    public void A_name_that_did_not_exist_before_is_deleted_again()
    {
        Assert.Equal(new WinlogonRestore(WinlogonRestoreKind.Delete), WinlogonNames.Restore("player", "player", null, previousSaved: true));
    }

    [Fact]
    public void Nothing_is_touched_without_a_saved_original_or_after_someone_else_changed_it()
    {
        Assert.Equal(WinlogonRestoreKind.Leave, WinlogonNames.Restore("player", "player", "Admin", previousSaved: false).Kind);
        Assert.Equal(WinlogonRestoreKind.Leave, WinlogonNames.Restore("someone", "player", "Admin", previousSaved: true).Kind);
        Assert.Equal(WinlogonRestoreKind.Leave, WinlogonNames.Restore(null, "player", "Admin", previousSaved: true).Kind);
    }
}
