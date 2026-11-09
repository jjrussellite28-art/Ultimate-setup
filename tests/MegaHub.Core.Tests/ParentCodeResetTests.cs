using MegaHub.Core.Config;
using MegaHub.Core.Guard;
using MegaHub.Core.Security;

namespace MegaHub.Core.Tests;

/// <summary>
/// 0.6.4 (security review M1): after the administrator's reset of a damaged code file the service holds no parents' code and the config's hash
/// is empty. Without a marker the parents' releases then fell back to "an unlocked couch is enough" (ParentGate) during the unlock's two minutes.
/// These tests run the same gate the Hud builds (ParentGate over ParentCodeState) through reset and new code.
/// </summary>
public sealed class ParentCodeResetTests
{
    private static AppConfig Config() => new() { CouchLock = "code", ParentalEnabled = true, PasscodeHash = "held-by-service" };

    private static (ParentGate Gate, ParentGrant Grant, int[] Unlocks) GateOver(AppConfig c)
    {
        var grant = new ParentGrant(() => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));
        var unlocks = new int[1];
        var gate = new ParentGate(grant,
            () => ParentCodeState.Has(c.CouchLock, c.PasscodeHash, false, c.ParentalEnabled, c.ParentCodeResetPending),
            () => unlocks[0]++);
        return (gate, grant, unlocks);
    }

    /// <summary>What the Hud does after the reset: the hash marker goes, and with parental controls on the pending marker is set.</summary>
    private static void Reset(AppConfig c, bool parentCodeGone = true)
    {
        ParentCodeState.ApplyReset(c, couchCodeGone: true, parentCodeGone, "held-by-service");
    }

    [Fact]
    public void After_the_reset_a_parents_release_needs_the_administrators_grant_not_the_open_couch()
    {
        var c = Config();
        var (gate, grant, unlocks) = GateOver(c);
        Reset(c);
        // The two minutes after the reset: the couch is unlocked (requireUnlocked would pass), but the release is refused until it is given.
        var ex = Assert.Throws<ParentCodeNeededException>(() => gate.Check("parentalOff", null));
        Assert.Equal(0, unlocks[0]);
        Assert.Throws<ParentCodeNeededException>(() => gate.Check("extend", "game:1"));
        Assert.Throws<ParentCodeNeededException>(() => gate.Check("pluginPage", "p/settings"));
        Assert.Equal("parentalOff", ex.Action);
        // The administrator's way for that one action (ParentByAdminAsync) gives the grant; it fits that action only and is used up once.
        grant.Give("parentalOff", null);
        Assert.Throws<ParentCodeNeededException>(() => gate.Check("extend", "game:1"));
        var ticket = gate.Check("parentalOff", null);
        Assert.NotNull(ticket);
        gate.Use(ticket);
        Assert.Throws<ParentCodeNeededException>(() => gate.Check("parentalOff", null));
    }

    [Fact]
    public void Without_the_marker_the_open_couch_would_have_been_enough_the_hole_this_closes()
    {
        var c = Config();
        var (gate, _, unlocks) = GateOver(c);
        c.PasscodeHash = "";
        Assert.Null(gate.Check("parentalOff", null));
        Assert.Equal(1, unlocks[0]);
    }

    [Fact]
    public void A_new_code_ends_the_marker_and_the_normal_code_applies()
    {
        var c = Config();
        var (gate, grant, unlocks) = GateOver(c);
        Reset(c);
        Assert.True(c.ParentCodeResetPending);
        ParentCodeState.CodeIsSet(c, "held-by-service");
        Assert.False(c.ParentCodeResetPending);
        Assert.Throws<ParentCodeNeededException>(() => gate.Check("parentalOff", null));
        grant.Give("parentalOff", null);
        Assert.NotNull(gate.Check("parentalOff", null));
        Assert.Equal(0, unlocks[0]);
        // The marker does not come back when the code is removed later.
        ParentCodeState.CodeIsSet(c, "");
        Assert.False(c.ParentCodeResetPending);
    }

    [Fact]
    public void The_marker_only_counts_with_parental_controls_on_in_the_code_lock()
    {
        var c = Config();
        c.ParentalEnabled = false;
        Reset(c);
        Assert.False(c.ParentCodeResetPending);
        var (gate, _, unlocks) = GateOver(c);
        Assert.Null(gate.Check("extend", null));
        Assert.Equal(1, unlocks[0]);
        // Set by the reset with parental on, then parental off: ignored (nothing to protect).
        var d = Config();
        Reset(d);
        d.ParentalEnabled = false;
        Assert.False(ParentCodeState.Has(d.CouchLock, d.PasscodeHash, false, d.ParentalEnabled, d.ParentCodeResetPending));
        // A service that still holds the parents' code (the file was valid again) sets nothing.
        var e = Config();
        Reset(e, parentCodeGone: false);
        Assert.False(e.ParentCodeResetPending);
    }

    [Fact]
    public void The_hash_and_the_marker_change_in_one_step_and_a_kept_couch_code_keeps_the_marker()
    {
        // Review MEDIUM-1: one call does both (the Hud passes it to ONE config write), so no state has the hash cleared without the marker.
        var c = Config();
        ParentCodeState.ApplyReset(c, couchCodeGone: true, parentCodeGone: true, "held-by-service");
        Assert.Equal("", c.PasscodeHash);
        Assert.True(c.ParentCodeResetPending);
        // A different hash (a local one) is not the service's marker and stays; parental off sets no marker.
        var local = Config();
        local.PasscodeHash = "pbkdf2-local";
        ParentCodeState.ApplyReset(local, true, true, "held-by-service");
        Assert.Equal("pbkdf2-local", local.PasscodeHash);
        // Review LOW-2: the couch code the service kept (no parents' code there) does not end the marker; a parents' code does.
        ParentCodeState.CodeIsSet(c, "held-by-service", parentCodePresent: false);
        Assert.True(c.ParentCodeResetPending);
        ParentCodeState.CodeIsSet(c, "held-by-service", parentCodePresent: true);
        Assert.False(c.ParentCodeResetPending);
        // The Hud's reset uses that single step and no separate clearing write of the hash.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MegaHub.slnx"))) root = Path.GetDirectoryName(root);
        var hud = File.ReadAllText(Path.Combine(root!, "src", "MegaHub.Shell", "Hud.cs")).Replace("\r\n", "\n");
        var at = hud.IndexOf("public async Task<object> ResetCodeStoreAsync(", StringComparison.Ordinal);
        var body = hud[at..hud.IndexOf("\n    }\n", at, StringComparison.Ordinal)];
        Assert.Contains("config.Update(c => MegaHub.Core.Security.ParentCodeState.ApplyReset(c, reset.CouchCodeGone, reset.ParentCodeGone, Passcode.HeldByService));", body);
        Assert.DoesNotContain("c.PasscodeHash =", body);
        Assert.DoesNotContain("ParentCodeResetPending", body);
    }

    [Fact]
    public void Nothing_in_the_bridge_phone_or_assistant_can_set_the_marker()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MegaHub.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var users = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (File.ReadAllText(file).Contains("ParentCodeResetPending", StringComparison.Ordinal)) users.Add(Path.GetFileName(file));
        }
        // Only the config, the Core rule and the Hud's reset know the marker: no UiBridge, setting key, plugin host or tool names it.
        Assert.Equal(["AppConfig.cs", "Hud.cs", "ParentCodeState.cs"], users.Order().ToArray());
    }
}
