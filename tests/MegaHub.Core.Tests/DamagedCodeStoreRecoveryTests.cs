using MegaHub.Core.Guard;

namespace MegaHub.Core.Tests;

/// <summary>
/// 0.6.4: a damaged code file of the service left the locked couch without a way out (every code was answered "the service could not check
/// it", and the reset was offered only in the settings, behind the lock). The code entries now offer the administrator's reset, and a code
/// lock without any code an administrator's approval. Pinned at the source: Hud and passcode.js need the service and WebView2; the decision
/// itself (<c>CodeFlow.AdminOpensCodeLock</c>) is tested in CodeFlowTests.
/// </summary>
public sealed class DamagedCodeStoreRecoveryTests
{
    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts])).Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MegaHub.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature);
        var rest = source[at..];
        var end = rest.IndexOf("\n    }\n", StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    [Fact]
    public void A_refused_couch_code_flags_the_reset_only_when_the_service_reports_the_damage()
    {
        var hud = ReadSource("src", "MegaHub.Shell", "Hud.cs");
        var unlock = Body(hud, "public async Task<object> UnlockAsync(");
        Assert.Contains("if (check is not { Granted: true }) return await LockCodeAnswerAsync(check);", unlock);
        var answer = Body(hud, "private static async Task<object> LockCodeAnswerAsync(");
        // Asked fresh after the refusal; a failed check only (a wrong code never offers anything), and a service that does not answer is no damage.
        Assert.Contains("check is { Result.Ok: false } && CodeFlow.RefusedForDamage(check.Result, await FreshServiceCodesAsync())", answer);
        Assert.Contains("codesDamaged = true", answer);
        Assert.Contains("check is { Result.Ok: true, Outcome: CodeCheckResults.NoCode }", answer);
        // Only flags: nothing opens here.
        Assert.DoesNotContain("_unlockedUntil", answer);
        Assert.DoesNotContain("ResetCodeStore", answer);
        // The parents' code: the same flag, and the administrator's way for the one action.
        var parent = Body(hud, "public async Task<object> ParentalAuthAsync(");
        Assert.Contains("!check.Result.Ok && CodeFlow.RefusedForDamage(check.Result, await FreshServiceCodesAsync())", parent);
        Assert.Contains("codesDamaged = true, admin = \"damaged\"", parent);
    }

    [Fact]
    public void The_administrators_unlock_opens_only_a_code_lock_without_any_code_and_checks_before_and_after_the_approval()
    {
        var hud = ReadSource("src", "MegaHub.Shell", "Hud.cs");
        var unlock = Body(hud, "public async Task<object> UnlockAsync(");
        // After the Windows mode (it returns first) and after the fail-closed check of the service, never before.
        var windows = unlock.IndexOf("config.Current.CouchLock == \"windows\"", StringComparison.Ordinal);
        var failClosed = unlock.IndexOf("FailClosed.MayUnlock(LockOn, service.State)", StringComparison.Ordinal);
        var byAdmin = unlock.IndexOf("if (byAdmin) return await UnlockByAdminAsync();", StringComparison.Ordinal);
        Assert.True(windows >= 0 && failClosed > windows && byAdmin > failClosed);
        Assert.True(byAdmin > unlock.IndexOf("if (ServiceCodes.Expected && !AdminService.DevMayRelax(dev))", StringComparison.Ordinal));
        var admin = Body(hud, "private async Task<object> UnlockByAdminAsync(");
        // The order (check, approval, check again) lives in CodeFlow.UnlockByAdminAsync and is tested behaviourally in CodeFlowTests; the Hud
        // hands it the fresh state and the UAC prompt and opens only for the outcome "Opened".
        Assert.Contains("CodeFlow.UnlockByAdminAsync(FreshServiceCodesAsync, () => AdminService.ObtainAdminGrantAsync([], AdminService.UnlockNoCodeReason))", admin);
        Assert.True(admin.IndexOf("CodeFlow.AdminUnlockOutcome.Opened", StringComparison.Ordinal) < admin.IndexOf("_unlockedUntil = MonoNow().AddMinutes(2);", StringComparison.Ordinal));
        Assert.DoesNotContain("AdminOpensCodeLock", admin);
        Assert.Contains("hud.UnlockAsync(Buttons(0), Arg(1).ValueKind == JsonValueKind.True, Arg(2).ValueKind == JsonValueKind.True)",
            ReadSource("src", "MegaHub.Shell", "UiHost", "UiBridge.cs"));
    }

    [Fact]
    public void The_reset_stays_damage_only_and_opens_the_code_lock_only_when_the_service_holds_no_code_afterwards()
    {
        var hud = ReadSource("src", "MegaHub.Shell", "Hud.cs");
        var reset = Body(hud, "public async Task<object> ResetCodeStoreAsync(");
        // Unchanged: only a damaged store, only through the administrator's approval of the service operation.
        // Damage-only, approved reset and the opening decision live in CodeFlow.ResetDamagedStoreAsync (tested in CodeFlowTests); the Hud passes
        // the service's fresh state, the approved reset and the code lock flag, and opens only for the outcome's OpensLock.
        var flow = reset.IndexOf("CodeFlow.ResetDamagedStoreAsync(ServiceCodes.RefreshAsync, () => ServiceCodes.ResetDamagedAsync(config.Current.CouchLock),", StringComparison.Ordinal);
        var notDamaged = reset.IndexOf("if (!reset.Reset) throw", StringComparison.Ordinal);
        var opens = reset.IndexOf("_unlockedUntil = MonoNow().AddMinutes(2);", StringComparison.Ordinal);
        Assert.True(flow >= 0 && notDamaged > flow && opens > notDamaged);
        Assert.Contains("config.Current.CouchLock == \"code\");", reset);
        Assert.Contains("if (reset.OpensLock)", reset);
        // Parental controls on: the parents' releases stay protected until a new code is set (security review M1).
        Assert.Contains("ParentCodeState.ApplyReset(c, reset.CouchCodeGone, reset.ParentCodeGone", reset);
        // ResetDamagedAsync throws when the service did not reset (declined prompt, refused, read error): nothing after it runs then.
        var service = ReadSource("src", "MegaHub.Shell", "Services", "ServiceCodes.cs");
        var resetDamaged = Body(service, "public static async Task ResetDamagedAsync(");
        Assert.Contains("AdminService.WithGrantAsync(Operation.ResetCodeStore", resetDamaged);
        Assert.Contains("if (!result.Ok) throw new InvalidOperationException(", resetDamaged);
        // Open on the locked couch, as before (it is the way out); nothing new on the bridge besides the unlock's third argument.
        Assert.Contains("resetCodeStore", LockPolicy.OpenWhileLocked);
        Assert.Contains("unlock", LockPolicy.OpenWhileLocked);
    }

    [Fact]
    public void The_code_entries_offer_the_reset_and_the_administrators_way()
    {
        var ui = ReadSource("ui", "components", "passcode.js");
        var couch = ui[ui.IndexOf("async function askForCode(", StringComparison.Ordinal)..ui.IndexOf("export function resetDamagedCodes(", StringComparison.Ordinal)];
        Assert.Contains("if (result?.codesDamaged) {", couch);
        Assert.Contains("const after = await resetDamagedCodes(result.blocked);", couch);
        // Open only when the host says so (its security view after the reset), never by the page's own guess.
        Assert.Contains("if (after?.unlocked) {", couch);
        Assert.Contains("result?.admin === 'noCode' && (await confirmDialog(t('passcode.adminUnlock')", couch);
        Assert.Contains("api.unlock(null, false, true)", couch);
        Assert.Contains("if (viaAdmin?.ok) {", couch);
        // One reset at a time, after the person's own choice; the host's answer or null.
        var reset = ui[ui.IndexOf("export function resetDamagedCodes(", StringComparison.Ordinal)..];
        Assert.Contains("codesReset ??=", reset);
        Assert.Contains("if (!(await confirmDialog(question, t('passcode.resetCodesConfirm')))) return null;", reset);
        Assert.Contains("api.resetCodeStore()", reset);
        var parent = ui[ui.IndexOf("export async function ensureParentCode(", StringComparison.Ordinal)..];
        Assert.Contains("if (result?.codesDamaged) {", parent);
        Assert.Contains("result = { ok: false, admin: 'noCode', blocked: t('passcode.resetCodesDone') };", parent);
        Assert.Contains("unlock: (code, asNewCode = false, byAdmin = false) => call('unlock', code, asNewCode, byAdmin)", ReadSource("ui", "bridge.js"));
    }
}
