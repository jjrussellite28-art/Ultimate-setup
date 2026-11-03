using System.Text.RegularExpressions;
using CouchHud.Core.Remote;
using CouchHud.Core.Security;
using Xunit;

namespace CouchHud.Core.Tests;

public class SecondFactorRulesTests
{
    private static RemoteDevice Phone(string? level, bool child = false, string key = "k") =>
        new() { Name = "p", TokenHash = new string('a', 64), Level = level, Child = child, Key = key, Profile = child ? "kid" : "default" };

    [Fact]
    public void Only_a_full_phone_that_is_not_a_childs_and_has_a_channel_may_confirm()
    {
        Assert.True(SecondFactorRules.MayConfirm(Phone("full"), "default", pcMode: false));
        Assert.False(SecondFactorRules.MayConfirm(Phone("library"), "default", false));
        Assert.False(SecondFactorRules.MayConfirm(Phone("control"), "default", false));
        Assert.False(SecondFactorRules.MayConfirm(Phone(null), "default", false));
        Assert.False(SecondFactorRules.MayConfirm(Phone("full", child: true), "kid", false), "a child's phone, whatever its level");
        Assert.False(SecondFactorRules.MayConfirm(Phone("full", key: ""), "default", false), "no sealed channel (paired before 0.7)");
        Assert.False(SecondFactorRules.MayConfirm(Phone("full"), "default", pcMode: true), "a plain remote in PC mode");
    }

    [Fact]
    public void The_second_factor_needs_the_code_check_and_a_confirming_phone()
    {
        var full = new[] { Phone("full") };
        Assert.Equal(SecondFactorBlock.None, SecondFactorRules.CanEnable("code", full));
        Assert.Equal(SecondFactorBlock.None, SecondFactorRules.CanEnable("windows", full));
        Assert.Equal(SecondFactorBlock.NoCodeCheck, SecondFactorRules.CanEnable("off", full));
        Assert.Equal(SecondFactorBlock.NoPhone, SecondFactorRules.CanEnable("code", []));
        Assert.Equal(SecondFactorBlock.NoPhone, SecondFactorRules.CanEnable("code", [Phone("library")]));
        Assert.Equal(SecondFactorBlock.NoPhone, SecondFactorRules.CanEnable("code", [Phone("full", child: true)]));
        Assert.Equal(SecondFactorBlock.NoPhone, SecondFactorRules.CanEnable("code", [Phone("full", key: "")]));
        Assert.False(SecondFactorRules.StillPossible("off", full));
        Assert.True(SecondFactorRules.StillPossible("code", full));
    }

    [Fact]
    public void Switching_the_code_off_warns_and_warns_more_with_the_second_factor_on()
    {
        Assert.Equal(CodeOffWarning.Plain, SecondFactorRules.WarnOnCodeOff("code", "off", secondFactorOn: false));
        Assert.Equal(CodeOffWarning.WithSecondFactor, SecondFactorRules.WarnOnCodeOff("code", "off", true));
        Assert.Equal(CodeOffWarning.WithSecondFactor, SecondFactorRules.WarnOnCodeOff("windows", "off", true));
        Assert.Equal(CodeOffWarning.None, SecondFactorRules.WarnOnCodeOff("code", "windows", true));
        Assert.Equal(CodeOffWarning.None, SecondFactorRules.WarnOnCodeOff("off", "off", true));
        Assert.Equal(CodeOffWarning.None, SecondFactorRules.WarnOnCodeOff("off", "code", false));
    }

    [Fact]
    public void Required_means_on_and_on_the_list()
    {
        Assert.True(SecondFactorRules.Required(true, "lockMode"));
        Assert.False(SecondFactorRules.Required(false, "lockMode"));
        Assert.False(SecondFactorRules.Required(true, "extend"));
        Assert.False(SecondFactorRules.Required(true, "LOCKMODE"));
        Assert.False(SecondFactorRules.Required(true, ""));
    }

    [Fact]
    public void An_administrator_account_in_admin_approval_mode_is_told_by_the_token_type()
    {
        Assert.False(SecondFactorRules.IsAdminAccount(0));
        Assert.False(SecondFactorRules.IsAdminAccount(1));
        Assert.True(SecondFactorRules.IsAdminAccount(2));
        Assert.True(SecondFactorRules.IsAdminAccount(3));
    }

    [Fact]
    public void The_detail_line_is_cleaned_and_cut()
    {
        Assert.Equal("", SecondFactorRules.CleanDetail(null));
        Assert.Equal("", SecondFactorRules.CleanDetail("   "));
        Assert.Equal("Kind", SecondFactorRules.CleanDetail("  Kind" + (char)13 + (char)10 + (char)9));
        Assert.Equal("ab", SecondFactorRules.CleanDetail("a" + (char)7 + "b"));
        var cut = SecondFactorRules.CleanDetail(new string('x', 200));
        Assert.Equal(SecondFactorRules.MaxDetail + 1, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void Only_a_higher_level_raises()
    {
        Assert.True(SecondFactorRules.Raises(null, "library"));
        Assert.True(SecondFactorRules.Raises("control", "full"));
        Assert.False(SecondFactorRules.Raises("full", "library"));
        Assert.False(SecondFactorRules.Raises("library", "library"));
        Assert.False(SecondFactorRules.Raises("full", "control"));
    }

    [Fact]
    public void The_phone_operations_are_for_the_full_level_only_and_the_event_too()
    {
        Assert.False(RemoteRights.Allows(RemoteLevel.Library, "secondFactor"));
        Assert.False(RemoteRights.Allows(RemoteLevel.Library, "secondFactorAnswer"));
        Assert.True(RemoteRights.Allows(RemoteLevel.Full, "secondFactor"));
        Assert.True(RemoteRights.Allows(RemoteLevel.Full, "secondFactorAnswer"));
        Assert.Equal(RemoteLevel.Full, RemoteRights.EventLevel("secondFactor"));
        Assert.Null(RemoteRights.Ops["secondFactor"].BridgeMethod);
    }
}

public class SecondFactorActionsTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    [Fact]
    public void The_list_has_unique_ids_texts_and_reasons_and_unknown_names_are_not_on_it()
    {
        Assert.Equal(SecondFactorActions.Critical.Count, SecondFactorActions.Critical.Select(a => a.Id).Distinct().Count());
        Assert.All(SecondFactorActions.Critical, a => Assert.False(string.IsNullOrWhiteSpace(a.Why)));
        Assert.All(SecondFactorActions.Critical, a => Assert.True(SecondFactorActions.IsCritical(a.Id)));
        Assert.False(SecondFactorActions.IsCritical(null));
        Assert.False(SecondFactorActions.IsCritical("launch"));
        Assert.False(SecondFactorActions.IsCritical("lockmode"));
    }

    [Fact]
    public void Every_critical_action_has_a_text_in_german_and_english()
    {
        foreach (var a in SecondFactorActions.Critical)
        {
            Assert.True(CouchHud.Core.Localization.Strings.German.ContainsKey(SecondFactorActions.TextKey(a.Id)), a.Id + " de");
            Assert.True(CouchHud.Core.Localization.Strings.English.ContainsKey(SecondFactorActions.TextKey(a.Id)), a.Id + " en");
        }
    }

    [Fact]
    public void Every_action_that_asks_a_code_is_classified_with_a_reason_or_an_action()
    {
        // The sources: the closed list of the HUD's own actions, the actions with service operations (Hud.ParentOps), and the actions of the
        // code in the shell (every literal handed to the grant functions).
        var known = new HashSet<string>(ParentCodeKind.HudOnlyActions) { "autologon", "lockMode", "lockModeCode", "parentalOn", "passcode", "parentalOff", "profile" };
        var root = Root();
        if (root.Length > 0)
        {
            var literal = new Regex("(?:RequireParentGrant|CheckParentGrant|_grant\\.(?:Fits|TryConsume|Give)|CodeAcceptedFor)\\(\"([A-Za-z]+)\"");
            foreach (var file in Directory.GetFiles(Path.Combine(root, "src", "CouchHud.Shell"), "*.cs", SearchOption.TopDirectoryOnly))
                foreach (Match m in literal.Matches(File.ReadAllText(file))) known.Add(m.Groups[1].Value);
            // The actions the pages name (ensureParentGrant / parentalAuth with { action: '…' }).
            var page = new Regex("action: '([A-Za-z]+)'");
            foreach (var file in Directory.GetFiles(Path.Combine(root, "ui"), "*.js", SearchOption.AllDirectories))
                foreach (Match m in page.Matches(File.ReadAllText(file))) known.Add(m.Groups[1].Value);
        }
        // Names the pages build from constants (profile switch/manage, plugin changes) are in HudOnlyActions already.
        var unclassified = known.Where(a => !SecondFactorActions.GrantActions.ContainsKey(a)).OrderBy(a => a).ToList();
        Assert.True(unclassified.Count == 0, "unclassified actions: " + string.Join(", ", unclassified) + " (SecondFactorActions.GrantActions)");
        Assert.All(SecondFactorActions.GrantActions, kv =>
        {
            if (kv.Value.Action is { } act) Assert.True(SecondFactorActions.IsCritical(act), $"{kv.Key} maps to {act}, which is not on the list");
            else Assert.False(string.IsNullOrWhiteSpace(kv.Value.Reason), $"{kv.Key} is left out without a reason");
        });
    }

    [Fact]
    public void Every_critical_action_is_guarded_at_least_once_in_the_shell()
    {
        var root = Root();
        if (root.Length == 0) return;
        var source = string.Concat(Directory.GetFiles(Path.Combine(root, "src", "CouchHud.Shell"), "*.cs").Select(File.ReadAllText));
        foreach (var a in SecondFactorActions.Critical)
        {
            var field = char.ToUpperInvariant(a.Id[0]) + a.Id[1..];
            Assert.Matches($@"RequireSecondFactor\w*\(\s*SecondFactorActions\.{field}\b|steps\.Add\(\(SecondFactorActions\.{field}\b", source);
        }
    }

    [Fact]
    public void Every_guard_in_the_shell_names_an_action_of_the_list()
    {
        var root = Root();
        if (root.Length == 0) return;
        var source = string.Concat(Directory.GetFiles(Path.Combine(root, "src", "CouchHud.Shell"), "*.cs").Select(File.ReadAllText));
        var ids = SecondFactorActions.Critical.Select(a => char.ToUpperInvariant(a.Id[0]) + a.Id[1..]).ToHashSet();
        foreach (Match m in Regex.Matches(source, @"(?:RequireSecondFactor\w*\(\s*|steps\.Add\(\()SecondFactorActions\.([A-Za-z]+)"))
            Assert.Contains(m.Groups[1].Value, ids);
        Assert.DoesNotContain("RequireSecondFactor(\"", source);
    }

    [Fact]
    public void The_service_operations_that_take_a_grant_are_all_classified_and_none_is_unlisted()
    {
        // Lists are checked against the service's table in CouchHud.Service.Tests (SecondFactorCoverageTests); here the entries are sound.
        Assert.All(SecondFactorActions.ServiceOperations, kv =>
        {
            if (kv.Value.Action is { } act) Assert.True(SecondFactorActions.IsCritical(act));
            else Assert.False(string.IsNullOrWhiteSpace(kv.Value.Reason));
        });
    }
}

public class SecondFactorCodeTableTests
{
    [Theory]
    // parental, parents' code held, grant fits, unlocked  ->  the site's code is in order
    [InlineData(false, true, false, true, true)]    // controls off, parents' code held: the unlocked couch is all the site wants -> the guard engages
    [InlineData(false, true, true, false, false)]   // locked: the usual refusal comes first
    [InlineData(false, false, false, true, true)]
    [InlineData(true, true, false, true, false)]    // controls on with a parents' code: the grant must fit
    [InlineData(true, true, true, false, true)]
    [InlineData(true, false, false, true, true)]    // controls on but no parents' code held: the unlocked couch (RequireParentGrant)
    [InlineData(true, false, false, false, false)]
    public void The_site_code_table(bool parental, bool held, bool fits, bool unlocked, bool expected) =>
        Assert.Equal(expected, SecondFactorRules.SiteCodeAccepted(parental, held, fits, unlocked));

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    public void The_gate_code_table(bool held, bool fits, bool unlocked, bool expected) =>
        Assert.Equal(expected, SecondFactorRules.CodeAccepted(held, fits, unlocked));

    [Fact]
    public void With_a_parents_code_held_and_the_controls_off_the_guard_is_never_skipped_for_want_of_a_grant()
    {
        // The review's case: HasParentCode && !ParentalEnabled. Whenever the couch is unlocked the guard waits for the phone; it is only
        // skipped (left to the usual code check) while the couch is locked.
        foreach (var fits in new[] { false, true })
        {
            Assert.True(SecondFactorRules.SiteCodeAccepted(false, true, fits, unlocked: true));
            Assert.False(SecondFactorRules.SiteCodeAccepted(false, true, fits, unlocked: false));
        }
    }
}
