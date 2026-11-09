using System.Text.RegularExpressions;
using MegaHub.Core.Guard;
using MegaHub.Core.Remote;
using MegaHub.Core.Security;

namespace MegaHub.Core.Tests;

/// <summary>0.7.1: a new pairing code takes the parents' code with parental controls on (one-time grant for exactly "remotePair").</summary>
public sealed class PairingGrantTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MegaHub.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "the repository root (MegaHub.slnx) was not found: the source checks cannot run");
        return dir!.FullName;
    }

    private static (ParentGate Gate, ParentGrant Grant, int[] Unlocks) Gate(bool hasParentCode = true)
    {
        var grant = new ParentGrant(() => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));
        var unlocks = new int[1];
        return (new ParentGate(grant, () => hasParentCode, () => unlocks[0]++), grant, unlocks);
    }

    /// <summary>What Hud.GetRemote does around the second factor: check, (second factor), use up.</summary>
    private static void Pair(ParentGate gate, bool newCode, bool parentalOn) => gate.Use(PairingGrant.Check(gate, newCode, parentalOn));

    [Fact]
    public void With_parental_controls_on_a_new_code_without_a_grant_is_refused()
    {
        var (gate, _, _) = Gate();
        var ex = Assert.Throws<ParentCodeNeededException>(() => Pair(gate, newCode: true, parentalOn: true));
        Assert.Equal(PairingGrant.Action, ex.Action);
        Assert.Null(ex.Item);
    }

    [Fact]
    public void A_grant_for_another_action_does_not_pair()
    {
        var (gate, grant, _) = Gate();
        grant.Give("networkListener", "remoteEnabled");
        Assert.Throws<ParentCodeNeededException>(() => Pair(gate, true, true));
        grant.Give("extend", "game:1");
        Assert.Throws<ParentCodeNeededException>(() => Pair(gate, true, true));
    }

    [Fact]
    public void With_the_grant_it_works_and_the_grant_cannot_be_used_twice()
    {
        var (gate, grant, _) = Gate();
        grant.Give(PairingGrant.Action);
        Pair(gate, true, true);
        Assert.Throws<ParentCodeNeededException>(() => Pair(gate, true, true));
    }

    [Fact]
    public void A_refused_try_keeps_the_grant_and_one_that_is_gone_by_use_time_refuses()
    {
        var (gate, grant, _) = Gate();
        grant.Give(PairingGrant.Action);
        // The check alone (a second factor that is still pending, a failed step) spends nothing.
        var ticket = PairingGrant.Check(gate, true, true);
        Assert.NotNull(ticket);
        Assert.True(grant.Fits(PairingGrant.Action));
        // Used by a call in between: the use refuses (no TOCTOU, fail-closed).
        Assert.True(grant.TryConsume(PairingGrant.Action));
        Assert.Throws<ParentCodeNeededException>(() => gate.Use(ticket));
    }

    [Fact]
    public void An_expired_grant_refuses()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var grant = new ParentGrant(() => clock, TimeSpan.FromSeconds(90));
        var gate = new ParentGate(grant, () => true, () => { });
        grant.Give(PairingGrant.Action);
        clock = now.AddSeconds(91);
        Assert.Throws<ParentCodeNeededException>(() => Pair(gate, true, true));
    }

    [Fact]
    public void With_parental_controls_off_pairing_needs_no_grant_and_asks_nothing()
    {
        var (gate, grant, unlocks) = Gate();
        Pair(gate, newCode: true, parentalOn: false);
        Assert.False(grant.Active);
        Assert.Null(PairingGrant.Check(gate, true, false));
        Assert.Equal(0, unlocks[0]);
    }

    [Fact]
    public void Showing_the_current_code_never_needs_a_grant()
    {
        var (gate, _, _) = Gate();
        Pair(gate, newCode: false, parentalOn: true);
        Assert.False(PairingGrant.NeedsParentGrant(false, true));
    }

    [Fact]
    public void Parental_controls_on_without_a_parents_code_fall_back_to_the_unlocked_couch()
    {
        var (gate, _, unlocks) = Gate(hasParentCode: false);
        Pair(gate, true, true);
        Assert.Equal(1, unlocks[0]);
    }

    private static Pairing NewPairing() => new(() => [], _ => { });

    [Fact]
    public void The_page_with_parental_controls_on_and_no_grant_shows_no_code_and_makes_none()
    {
        var pairing = NewPairing();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(pairing.PeekCode(now));
        Assert.Null(PairingGrant.CodeToShow(true, pairing.PeekCode(now), issued: null));
        Assert.Null(pairing.PeekCode(now)); // peeking mints nothing
        // A code that exists but was not made under the grant (made while parental controls were off) is not shown either.
        var old = pairing.CurrentCode(now);
        Assert.Null(PairingGrant.CodeToShow(true, pairing.PeekCode(now), issued: null));
        Assert.Equal(old, PairingGrant.CodeToShow(false, pairing.PeekCode(now), issued: null));
    }

    [Fact]
    public void After_a_granted_new_code_it_is_shown_and_after_pairing_or_expiry_it_is_hidden_again()
    {
        var now = DateTimeOffset.UtcNow;
        var pairing = NewPairing();
        var issued = pairing.NewCode(now);
        Assert.Equal(issued, PairingGrant.CodeToShow(true, pairing.PeekCode(now), issued));
        // Used by a phone: gone, nothing shown (and nothing re-made).
        Assert.NotNull(pairing.TryPair(issued, "phone", now));
        Assert.Null(PairingGrant.CodeToShow(true, pairing.PeekCode(now), issued));
        Assert.Null(pairing.PeekCode(now));
        // Expiry.
        var second = pairing.NewCode(now);
        Assert.Equal(second, PairingGrant.CodeToShow(true, pairing.PeekCode(now.AddMinutes(9)), second));
        Assert.Null(PairingGrant.CodeToShow(true, pairing.PeekCode(now.Add(Pairing.CodeLifetime)), second));
    }

    [Fact]
    public void Racing_calls_get_exactly_one_use_out_of_one_grant()
    {
        var grant = new ParentGrant(() => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));
        for (var round = 0; round < 200; round++)
        {
            grant.Give(PairingGrant.Action);
            var wins = 0;
            using var gate = new Barrier(8);
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                gate.SignalAndWait();
                if (grant.TryConsume(PairingGrant.Action)) Interlocked.Increment(ref wins);
            });
            Assert.Equal(1, wins);
        }
    }

    [Fact]
    public void The_action_is_classified_and_the_page_keeps_the_same_rule()
    {
        Assert.Equal(SecondFactorActions.RemotePair, SecondFactorActions.GrantActions[PairingGrant.Action].Action);
        var js = File.ReadAllText(Path.Combine(Root(), "ui", "core", "net-rules.js"));
        Assert.Contains("pairingNeedsParent = (newCode, parentalOn) => !!newCode && !!parentalOn", js);
        Assert.Contains("action: '" + PairingGrant.Action + "'", File.ReadAllText(Path.Combine(Root(), "ui", "views", "phone.js")));
    }

    [Fact]
    public void GetRemote_checks_the_grant_before_the_window_and_uses_it_up_before_the_code_is_made()
    {
        var hud = File.ReadAllText(Path.Combine(Root(), "src", "MegaHub.Shell", "Hud.cs"));
        var start = hud.IndexOf("public object GetRemote(bool newCode = false)", StringComparison.Ordinal);
        Assert.True(start > 0, "GetRemote not found");
        var end = hud.IndexOf("return new", start, StringComparison.Ordinal);
        var body = hud[start..end];
        var unlocked = body.IndexOf("RequireUnlocked();", StringComparison.Ordinal);
        var check = body.IndexOf("PairingGrant.Check(Gate, newCode, parentalOn)", StringComparison.Ordinal);
        var window = body.IndexOf("OpenPairingWindow();", StringComparison.Ordinal);
        var use = body.IndexOf("UseUp(pairTicket);", StringComparison.Ordinal);
        var made = body.IndexOf("Pairing.NewCode(", StringComparison.Ordinal);
        Assert.True(unlocked >= 0 && check > unlocked && window > check && use > window && made > use, "order: unlock, check, window, use up, make the code");
        // The grant is spent only inside the branch that makes the code (after RemoteEnabled, address, Locked and pairLocked), and a failed use closes the window.
        Assert.True(body.IndexOf("if (c.RemoteEnabled && address is not null && !Locked && !pairLocked)", StringComparison.Ordinal) is var gateAt && gateAt >= 0 && use > gateAt, "use up only after the eligibility checks");
        Assert.Contains("_pairOpenUntil = DateTimeOffset.MinValue;", body[use..made]);
        // With parental controls on the page never calls CurrentCode.
        Assert.Contains("PairingGrant.CodeToShow(true, Pairing.PeekCode(now), _issuedPairCode)", body);
        Assert.Single(Regex.Matches(body, @"Pairing\.CurrentCode\("));
        Assert.Matches(@"else if \(!parentalOn\) code = Pairing\.CurrentCode\(now\);", body);
        // Only GetRemote(true) from the HUD's own page makes a new code; nothing else calls it with true.
        var callers = Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"GetRemote\(\s*(?:newCode:\s*)?true\s*\)").Select(_ => f)).ToList();
        Assert.Empty(callers);
    }

    [Fact]
    public void Leaving_the_page_drops_the_issued_code_and_nothing_shows_it_again()
    {
        var now = DateTimeOffset.UtcNow;
        var devices = new List<RemoteDevice>();
        var pairing = new Pairing(() => devices, change => change(devices));
        var issued = pairing.NewCode(now);
        Assert.Equal(issued, PairingGrant.CodeToShow(true, pairing.PeekCode(now), issued));
        // Another code than the live one is not dropped (it is not the one the page showed).
        Assert.False(pairing.Discard("other"));
        Assert.False(pairing.Discard(null));
        Assert.Equal(issued, pairing.PeekCode(now));
        Assert.True(pairing.Discard(issued));
        Assert.Null(pairing.PeekCode(now));
        Assert.Null(PairingGrant.CodeToShow(true, pairing.PeekCode(now), issued));
        Assert.Null(pairing.TryPair(issued, "Phone", now));
        Assert.Empty(devices);
        Assert.False(pairing.Discard(issued));
    }

    [Fact]
    public void The_host_forgets_the_issued_code_on_leaving_locking_and_a_profile_switch()
    {
        var shell = Path.Combine(Root(), "src", "MegaHub.Shell");
        var hud = File.ReadAllText(Path.Combine(shell, "Hud.cs"));
        Assert.Contains("public void LeavePairing() => ForgetPairCode(true);", hud);
        Assert.Contains("Interlocked.Exchange(ref _issuedPairCode, null)", hud);
        Assert.Matches(@"ClearUnlock\(\);[^}]*ForgetPairCode\(true\);", Regex.Replace(hud, @"\s+", " "));
        Assert.Contains("ForgetPairCode(true);", File.ReadAllText(Path.Combine(shell, "HudProfiles.cs")));
        // A reloaded or crashed page calls no onClose: the host resets its screen state on every navigation start and process failure.
        Assert.Matches(@"ResetUiScreenState\(\)\s*\{[^}]*ForgetPairCode\(true\);", File.ReadAllText(Path.Combine(shell, "HudScreenshots.cs")));
        var window = File.ReadAllText(Path.Combine(shell, "MainWindow.xaml.cs"));
        Assert.Matches(@"NavigationStarting.{0,300}?_hud\.ResetUiScreenState\(\)", Regex.Replace(window, @"\s+", " "));
        Assert.Matches(@"RenderProcessExited[^}]*?_hud\.ResetUiScreenState\(\)", Regex.Replace(window, @"\s+", " "));
        Assert.Matches(@"case ""leavePairing"":\s*hud\.LeavePairing\(\);", File.ReadAllText(Path.Combine(shell, "UiHost", "UiBridge.cs")));
        Assert.Contains("leavePairing", LockPolicy.OpenWhileLocked);
    }
}
