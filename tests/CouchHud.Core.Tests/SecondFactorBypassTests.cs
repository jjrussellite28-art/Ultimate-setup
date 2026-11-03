using System.Text.RegularExpressions;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>
/// The bypasses the review of the second factor found (H-1 to H-5, M-1, M-3, M-5), each closed by a statement about the sources of the
/// methods involved: the guard is there, it is the right one and it comes before the effect (and before the code is used up).
/// </summary>
public class SecondFactorBypassTests
{
    private static string ShellDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) dir = dir.Parent;
        return dir is null ? "" : Path.Combine(dir.FullName, "src", "CouchHud.Shell");
    }

    private static string Body(string name)
    {
        foreach (var file in Directory.GetFiles(ShellDir(), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"^    (?:\[[^\]\r\n]*\]\s*\r?\n    )*(?:public|private|internal)[^;=\r\n(]*?\b" + Regex.Escape(name) + @"\s*\(", RegexOptions.Multiline))
            {
                var open = text.IndexOf('(', m.Index + m.Length - 1);
                var depth = 0;
                var i = open;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')' && --depth == 0) break;
                }
                var after = i + 1;
                while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                if (after >= text.Length) continue;
                if (text[after] == '{')
                {
                    var braces = 0;
                    var inString = false;
                    for (var j = after; j < text.Length; j++)
                    {
                        var c = text[j];
                        if (inString)
                        {
                            if (c == '\\') j++;
                            else if (c == '"') inString = false;
                        }
                        else if (c == '/' && j + 1 < text.Length && text[j + 1] == '/') j = text.IndexOf('\n', j);
                        else if (c == '"') inString = true;
                        else if (c == '{') braces++;
                        else if (c == '}' && --braces == 0) return text[after..(j + 1)];
                        if (j < 0) break;
                    }
                }
                else if (text.AsSpan(after).StartsWith("=>")) return text[after..text.IndexOf(";\n", after, StringComparison.Ordinal)];
            }
        }
        throw new InvalidOperationException("method not found: " + name);
    }

    private static void Before(string body, string guard, string effect)
    {
        var g = body.IndexOf(guard, StringComparison.Ordinal);
        var e = body.IndexOf(effect, StringComparison.Ordinal);
        Assert.True(g >= 0, $"no {guard}");
        Assert.True(e < 0 || g < e, $"{guard} must come before {effect}");
    }

    private static bool Present => ShellDir().Length > 0;

    [Fact]
    public void H1_a_new_couch_code_under_the_same_lock_mode_takes_the_passcode_guard()
    {
        if (!Present) return;
        var body = Body("SetCouchLockAsync");
        Assert.Contains("else if (sequence is not null) RequireSecondFactor(SecondFactorActions.Passcode", body);
        Assert.Contains("if (config.Current.CouchLock != \"off\")", body);
        Before(body, "SecondFactorActions.Passcode", "RequireParentGrant(");
        Before(body, "SecondFactorActions.LockMode", "Passcode.Validate(sequence)");
    }

    [Fact]
    public void H2_light_mode_asks_one_question_for_shell_lock_and_task_manager_before_it_touches_anything()
    {
        if (!Present) return;
        var body = Body("SetIntegrationMode");
        foreach (var part in new[] { "SecondFactorActions.Shell", "SecondFactorActions.LockMode", "SecondFactorActions.TaskManager", "RequireSecondFactorAll(", "ReconcileSecondFactor()" })
            Assert.Contains(part, body);
        Before(body, "RequireSecondFactorAll(", "shell.UninstallAsShell()");
        Before(body, "RequireSecondFactorAll(", "c.CouchLock = \"off\"");
        Before(body, "RequireSecondFactorAll(", "TaskManagerPolicy.Apply(false)");
        // Each step only when it really changes something.
        Assert.Contains("if (shell.IsInstalledAsShell()) steps.Add", body);
        Assert.Contains("if (config.Current.CouchLock != \"off\") steps.Add", body);
        Assert.Contains("if (config.Current.BlockTaskManager) steps.Add", body);
    }

    [Fact]
    public void H3_the_parents_code_chain_opens_only_for_a_first_parents_code_when_the_second_factor_is_on()
    {
        if (!Present) return;
        // Switching the controls on opens the chain; the code it sets is asked the second factor in the chain too (not only outside it).
        Assert.Contains("if (on) _parentCodeChainUntil = MonoNow().AddMinutes(2);", Body("SetParentalCore"));
        var set = Body("SetParentCodeAsync");
        Assert.Contains("if (inChain && config.Current.CouchLock != \"off\") RequireSecondFactor(SecondFactorActions.Passcode, true, \"couch\");", set);
        // Before the chain is used up (the page's repeat after the confirmation finds it) and before the code is stored.
        Before(set, "if (inChain &&", "_parentCodeChainUntil = DateTimeOffset.MinValue");
        Before(set, "if (inChain &&", "StoreAsync(");
        Before(set, "SecondFactorActions.ParentCode", "StoreAsync(");
        Assert.Contains("if (!inChain)", set);
    }

    [Fact]
    public void H4_installing_is_approving_local_package_other_catalog_capability_update_source_and_auto_update()
    {
        if (!Present) return;
        var install = Body("InstallPackageAsync");
        Assert.Contains("SecondFactorActions.PluginRights", install);
        Assert.Contains("SHA256.HashData(zip.ToArray())", install);
        Before(install, "SecondFactorActions.PluginRights", "UseUp(ticket)");
        Before(install, "SecondFactorActions.PluginRights", "PackageInstaller.Install(");

        var catalog = Body("InstallCatalogEntryAsync");
        Assert.Contains("request.Origin != PluginOrigin.FirstParty || request.AddsCapabilities", catalog);
        Assert.Contains("request.Entry.Sha256", catalog);
        Before(catalog, "RequireSecondFactorFor(SecondFactorActions.PluginRights", "UseUp(change)");

        var source = Body("AddCatalogSource");
        Before(source, "SecondFactorActions.PluginRights", "config.UpdateOrThrow");

        Assert.Contains("key == \"pluginAutoUpdate\" && netOn", Body("SetSettingAsync"));
    }

    [Fact]
    public void H5_quitting_as_the_shell_or_with_no_desktop_is_pc_mode()
    {
        if (!Present) return;
        var body = Body("QuitFromUi");
        Assert.Contains("shell.IsInstalledAsShell() || !ShellManager.IsDesktopShellRunning()", body);
        Before(body, "SecondFactorActions.PcMode", "Quit()");
        // The restart comes straight back (no desktop in between): it stays without a guard on purpose.
        Assert.Contains("Quit(restarting: true)", Body("RestartFromUi"));
    }

    [Fact]
    public void M1_leaving_a_childs_profile_for_one_without_limits_takes_the_guard_before_the_code_is_used_up()
    {
        if (!Present) return;
        var body = Body("SwitchProfile");
        Assert.Contains("SecondFactorActions.ProfileUnrestrict", body);
        Assert.Contains("ProfileSwitch.IsChild(now, now.ActiveProfile.Id) && !ProfileSwitch.IsChild(now, id)", body);
        Before(body, "SecondFactorActions.ProfileUnrestrict", "switch (decision.Need)");
    }

    [Fact]
    public void M3_the_question_and_tickets_go_when_phones_or_the_profile_change_or_pc_mode_starts()
    {
        if (!Present) return;
        foreach (var method in new[] { "RevokeRemote", "RevokeRemoteDevice", "SetRemoteDeviceLevel", "ActivateProfile", "EnterPcModeCoreAsync" })
            Assert.Contains("ResetSecondFactor()", Body(method));
    }

    [Fact]
    public void M5_the_codes_the_guard_waits_for_are_exactly_the_ones_the_site_asks_for()
    {
        if (!Present) return;
        // No hand-written "ParentalEnabled ? _grant.Fits(...) : IsUnlocked" any more: it skipped the guard where the site let the action through.
        var source = string.Concat(Directory.GetFiles(ShellDir(), "*.cs").Select(File.ReadAllText));
        Assert.DoesNotMatch(@"RequireSecondFactor\w*\([^;]*ParentalEnabled \? _grant\.Fits", source);
        Assert.Contains("SiteCodeAccepted(\"privacy\", key)", Body("SetSettingAsync"));
        Assert.Contains("SiteCodeAccepted(\"passcode\")", Body("SetPasscodeAsync"));
        Assert.Contains("SiteCodeAccepted(grant)", Body("SetCouchLockAsync"));
    }

    [Fact]
    public void L1_the_pairing_window_is_taken_and_closed_in_one_step_under_a_lock()
    {
        if (!Present) return;
        var source = File.ReadAllText(Path.Combine(ShellDir(), "HudSecondFactor.cs"));
        var begin = source[source.IndexOf("IRemoteChannelTarget.BeginPairing()", StringComparison.Ordinal)..source.IndexOf("IRemoteChannelTarget.EndPairing(", StringComparison.Ordinal)];
        Assert.Contains("lock (_pairLock)", begin);
        Assert.Contains("_pairBusy", begin);
        var end = source[source.IndexOf("IRemoteChannelTarget.EndPairing(", StringComparison.Ordinal)..source.IndexOf("PairingWindowClosed()", StringComparison.Ordinal)];
        Assert.Contains("lock (_pairLock)", end);
        Assert.Contains("lock (_pairLock)", Body("ResetSecondFactor"));
    }

    [Fact]
    public void L2_the_challenge_object_exists_from_the_start()
    {
        if (!Present) return;
        var source = File.ReadAllText(Path.Combine(ShellDir(), "HudSecondFactor.cs"));
        Assert.Contains("private readonly SecondFactorChallenges Factor = new(MonoNow, null, Path.Combine(dataDir, \"second-factor-state.json\"));", source);
        Assert.DoesNotContain("_secondFactor ??=", source);
    }

    [Fact]
    public void The_main_switch_takes_a_real_true_or_false_and_the_public_entry_guards_switching_off()
    {
        if (!Present) return;
        // A number, a text or null used to fall through as "off" without the guard.
        var refuse = Body("RefuseBadSwitchValue");
        Assert.Contains("key == \"enabled\"", refuse);
        Assert.Contains("JsonValueKind.True or System.Text.Json.JsonValueKind.False", refuse);
        Assert.Contains("throw new ArgumentException", refuse);
        var publicEntry = Body("SetParental");
        Before(publicEntry, "RefuseBadSwitchValue(key, value)", "SetParentalCore(");
        Before(publicEntry, "SecondFactorActions.ParentalOff", "SetParentalCore(");
        var asyncEntry = Body("SetParentalAsync");
        Before(asyncEntry, "RefuseBadSwitchValue(key, value)", "RequireSecondFactor(");
        Before(asyncEntry, "SecondFactorActions.ParentalOff", "ServiceCodes.RemoveAsync(");
        Before(asyncEntry, "SecondFactorActions.ParentalOff", "SetParentalCore(");
    }

    [Theory]
    [InlineData("SetPasscodeAsync", "SiteCodeAccepted(\"passcode\")")]
    [InlineData("SetCouchLockAsync", "SiteCodeAccepted(grant)")]
    [InlineData("SetSettingAsync", "SiteCodeAccepted(\"privacy\", key)")]
    public void Where_a_site_asks_for_a_code_only_with_parental_controls_the_guard_waits_for_exactly_that(string method, string accepted)
    {
        if (!Present) return;
        // A parents' code held with parental controls OFF: the site wants the unlocked couch only, so the guard must engage (the truth table
        // is in SecondFactorCodeTableTests); here the sites are pinned to the function that has the right table.
        Assert.Contains(accepted, Body(method));
    }

    [Fact]
    public void F1_the_way_back_and_the_release_of_the_lock_policies_take_the_task_manager_guard_first()
    {
        if (!Present) return;
        var release = Body("ReleaseLockPoliciesAsync");
        Assert.Contains("RequireSecondFactor(SecondFactorActions.TaskManager, IsUnlocked", release);
        Before(release, "SecondFactorActions.TaskManager", "PolicyHelper.Freeze()");
        Before(release, "SecondFactorActions.TaskManager", "ReleaseLockPoliciesNow");
        var setup = Body("SetupServiceTaskAsync");
        Assert.Contains("if (!policies) RequireSecondFactor(SecondFactorActions.TaskManager, IsUnlocked", setup);
        Before(setup, "SecondFactorActions.TaskManager", "config.UpdateOrThrow(c => c.CouchServices.Clear())");
        Before(setup, "SecondFactorActions.TaskManager", "ReleaseLockPoliciesNow");
        // The release itself says its callers guard; the walk checks them.
        Assert.Contains("CallersGuard = true", File.ReadAllText(Path.Combine(ShellDir(), "Hud.cs")).Split("private PolicyRelease.Verdict ReleaseLockPoliciesNow()")[0].Split("[SecondFactorExempt(")[^1]);
    }

    [Fact]
    public void F3_the_comment_in_SetParentalAsync_says_the_core_asks_nothing_again()
    {
        if (!Present) return;
        var text = File.ReadAllText(Path.Combine(ShellDir(), "Hud.cs"));
        Assert.DoesNotContain("the core below guards again", text);
        Assert.Contains("The core below asks nothing again", text);
    }
}
