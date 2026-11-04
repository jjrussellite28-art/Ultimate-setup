using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Companion apps: who may be one, which start with a game, and the refusals that keep them from being a way around the lock and the parental rules.</summary>
public class CompanionRulesTests
{
    private const string Win = @"C:\Windows";

    private static LibraryItem App(string id, string path, ItemType type = ItemType.App, bool embedded = false) => new()
    {
        Id = id,
        Source = "custom",
        Name = id,
        Type = type,
        Launch = new LaunchTarget(LaunchKind.Exe, path) { Embedded = embedded }
    };

    private static LibraryItem Game(string id) => App(id, @"D:\Games\" + id + ".exe", ItemType.Game);

    // The real path is the asked one here (the files do not exist); CompanionPathTests covers links, junctions and short names on real files.
    private static string? Same(string path) => path;

    private static CompanionVerdict Verdict(LibraryItem app) => CompanionRules.Eligible(app, app.Launch, Win, Same);

    // ---- who may be a companion

    [Fact]
    public void A_plain_program_is_eligible()
    {
        Assert.Equal(CompanionVerdict.Ok, Verdict(App("wand", @"C:\Users\Anna\AppData\Local\Wand\Wand.exe")));
        Assert.Equal(CompanionVerdict.Ok, Verdict(App("discord", @"C:\Program Files\Discord\Discord.exe")));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", CompanionVerdict.SystemFolder)]
    [InlineData(@"C:\Tools\cmd.exe", CompanionVerdict.Tool)]
    [InlineData(@"C:\Tools\powershell.exe", CompanionVerdict.Tool)]
    [InlineData(@"C:\Tools\python.exe", CompanionVerdict.Tool)]
    [InlineData(@"C:\Tools\explorer.exe", CompanionVerdict.Tool)]
    [InlineData(@"C:\Program Files\Steam\steam.exe", CompanionVerdict.Tool)]
    [InlineData(@"C:\Program Files\Google\Chrome\chrome.exe", CompanionVerdict.Browser)]
    [InlineData(@"C:\Tools\run.bat", CompanionVerdict.Script)]
    [InlineData(@"C:\Tools\RUN.CMD", CompanionVerdict.Script)]
    [InlineData(@"\\server\share\tool.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"\\?\C:\Tools\tool.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"tool.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"..\tool.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\tool.com", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\to""ol.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\a.txt:x.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\tool.exe:stream", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\*.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\to?l.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\..\Windows\System32\cmd.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\.\tool.exe", CompanionVerdict.NoProgram)]
    [InlineData(@"C:\Tools\tool.exe.", CompanionVerdict.NoProgram)]
    public void A_shell_a_tool_a_browser_a_script_or_a_remote_file_is_no_companion(string path, CompanionVerdict expected) =>
        Assert.Equal(expected, Verdict(App("x", path)));

    [Fact]
    public void A_game_a_web_page_a_protocol_and_a_store_app_are_no_companions()
    {
        Assert.Equal(CompanionVerdict.NotAnApp, Verdict(Game("elden")));
        Assert.Equal(CompanionVerdict.Web, Verdict(App("tube", "https://youtube.com", embedded: true)));
        Assert.Equal(CompanionVerdict.Web, CompanionRules.Eligible(App("tube", "x"), new LaunchTarget(LaunchKind.Url, "https://youtube.com"), Win, Same));
        Assert.Equal(CompanionVerdict.Packaged, CompanionRules.Eligible(App("store", "x"), new LaunchTarget(LaunchKind.Url, @"shell:AppsFolder\Contoso.Notes_8wekyb3d8bbwe!App"), Win, Same));
        Assert.Equal(CompanionVerdict.Packaged, CompanionRules.Eligible(App("proto", "x"), new LaunchTarget(LaunchKind.Url, "steam://run/1245620"), Win, Same));
    }

    [Fact]
    public void The_start_the_library_runs_decides_not_the_scanned_one()
    {
        // The user's own start target (a batch file, a shell) is what runs: the app is judged on that.
        var app = App("wand", @"C:\Wand\Wand.exe");
        Assert.Equal(CompanionVerdict.Script, CompanionRules.Eligible(app, new LaunchTarget(LaunchKind.Exe, @"C:\Wand\go.bat"), Win, Same));
        Assert.Equal(CompanionVerdict.Tool, CompanionRules.Eligible(app, new LaunchTarget(LaunchKind.Exe, @"C:\Tools\cmd.exe") { Args = ["/c", "wand"] }, Win, Same));
    }

    // ---- the rule list

    [Fact]
    public void A_hand_edited_rule_list_is_cut_down_to_what_is_valid()
    {
        var rules = CompanionRules.Sanitize([
            null,
            new CompanionRule { AppId = "  ", Mode = "all" },
            new CompanionRule { AppId = "a\u0001b" },
            new CompanionRule { AppId = "wand", Mode = "everything", GameIds = ["g1", "g1", "", "wand", "g\n2", "g3"] },
            new CompanionRule { AppId = "wand", Mode = "all" },
            new CompanionRule { AppId = "discord", Mode = "all", CloseWithGame = true, WhenLocked = true }
        ]);
        Assert.Equal(["wand", "discord"], rules.Select(r => r.AppId));
        // An unknown mode is the narrow one, never "every game".
        Assert.Equal(CompanionRule.ModeGames, rules[0].Mode);
        Assert.Equal(["g1", "g3"], rules[0].GameIds);
        Assert.True(rules[1].CloseWithGame && rules[1].WhenLocked && rules[1].Mode == CompanionRule.ModeAll);
        // Safe defaults: nothing ends and nothing starts locked unless the rule says so.
        var plain = CompanionRules.Sanitize([new CompanionRule { AppId = "x" }])[0];
        Assert.False(plain.CloseWithGame);
        Assert.False(plain.WhenLocked);
        Assert.True(plain.Minimized);
    }

    [Fact]
    public void The_lists_are_bounded()
    {
        var many = Enumerable.Range(0, 100).Select(i => new CompanionRule { AppId = "a" + i, Mode = "games", GameIds = Enumerable.Range(0, 300).Select(g => "g" + g).ToList() }).ToList();
        var cleaned = CompanionRules.Sanitize(many);
        Assert.Equal(CompanionRules.MaxRules, cleaned.Count);
        Assert.All(cleaned, r => Assert.Equal(CompanionRules.MaxGames, r.GameIds.Count));
        Assert.Empty(CompanionRules.Sanitize([new CompanionRule { AppId = new string('x', 5000) }]));
    }

    // ---- which companions start with a game

    private static CompanionPlan Plan(LibraryItem game, IReadOnlyList<CompanionRule> rules, IReadOnlyList<LibraryItem> library,
        bool locked = false, Func<LibraryItem, string?>? gate = null, Func<LibraryItem, LaunchTarget>? effective = null) =>
        CompanionRules.Plan(game, rules, id => library.FirstOrDefault(i => i.Id == id), effective ?? (i => i.Launch), gate ?? (_ => null), locked, Win, Same);

    [Fact]
    public void All_mode_starts_with_every_game_and_games_mode_only_with_the_named_ones()
    {
        var wand = App("wand", @"C:\Wand\Wand.exe");
        var discord = App("discord", @"C:\Discord\Discord.exe");
        var elden = Game("elden");
        var hades = Game("hades");
        var rules = new[]
        {
            new CompanionRule { AppId = "wand", Mode = "games", GameIds = ["elden"] },
            new CompanionRule { AppId = "discord", Mode = "all" }
        };
        var library = new[] { wand, discord, elden, hades };
        Assert.Equal(["wand", "discord"], Plan(elden, rules, library).Start.Select(s => s.App.Id));
        Assert.Equal(["discord"], Plan(hades, rules, library).Start.Select(s => s.App.Id));
    }

    [Fact]
    public void An_app_that_is_no_game_starts_nothing_and_neither_does_the_game_itself()
    {
        var wand = App("wand", @"C:\Wand\Wand.exe");
        var rules = new[] { new CompanionRule { AppId = "wand", Mode = "all" } };
        Assert.Empty(Plan(wand, rules, [wand]).Start);
        var self = Game("loop");
        Assert.Empty(Plan(self, [new CompanionRule { AppId = "loop", Mode = "all" }], [self]).Start);
    }

    private static readonly string Pin = CompanionPin.Version + new string('a', 64);

    [Fact]
    public void A_locked_couch_starts_only_a_companion_whose_file_hash_was_confirmed()
    {
        var wand = App("wand", @"C:\Users\kid\Games\Wand.exe");
        var game = Game("elden");
        var unpinned = new[] { new CompanionRule { AppId = "wand", Mode = "all", WhenLocked = true } };
        var plan = Plan(game, unpinned, [wand, game], locked: true);
        Assert.Empty(plan.Start);
        Assert.Equal("unpinned", Assert.Single(plan.Skipped).Reason);
        var confirmed = new[] { new CompanionRule { AppId = "wand", Mode = "all", WhenLocked = true, LockedSha256 = Pin } };
        var start = Assert.Single(Plan(game, confirmed, [wand, game], locked: true).Start);
        Assert.True(start.Locked);
        // A signature rule (Sha256) needs no separate confirmation.
        var signed = new[] { new CompanionRule { AppId = "wand", Mode = "all", WhenLocked = true, Sha256 = new string('a', 64) } };
        Assert.Single(Plan(game, signed, [wand, game], locked: true).Start);
        // An open couch is judged by the person's own right, as before; the start is not marked locked, so the host compares no hash.
        Assert.False(Assert.Single(Plan(game, unpinned, [wand, game], locked: false).Start).Locked);
        Assert.False(Assert.Single(Plan(game, confirmed, [wand, game], locked: false).Start).Locked);
    }

    [Fact]
    public void A_hand_edited_locked_hash_that_is_no_pin_counts_as_not_confirmed()
    {
        var rules = CompanionRules.Sanitize([
            new CompanionRule { AppId = "a", LockedSha256 = "nothex" },
            new CompanionRule { AppId = "b", LockedSha256 = CompanionPin.Version + new string('F', 64) },
            new CompanionRule { AppId = "c", LockedSha256 = new string('f', 64) }   // the old format: a bare hash
        ]);
        Assert.Equal(["", CompanionPin.Version + new string('f', 64), ""], rules.Select(r => r.LockedSha256));
    }

    [Fact]
    public void A_locked_couch_starts_none_unless_the_rule_says_so_and_never_a_tool()
    {
        var wand = App("wand", @"C:\Wand\Wand.exe");
        var shell = App("shell", @"C:\Tools\cmd.exe");
        var game = Game("elden");
        var library = new[] { wand, shell, game };
        var closed = new[] { new CompanionRule { AppId = "wand", Mode = "all" } };
        var plan = Plan(game, closed, library, locked: true);
        Assert.Empty(plan.Start);
        Assert.Equal("locked", Assert.Single(plan.Skipped).Reason);
        var open = new[] { new CompanionRule { AppId = "wand", Mode = "all", WhenLocked = true, LockedSha256 = Pin }, new CompanionRule { AppId = "shell", Mode = "all", WhenLocked = true, LockedSha256 = Pin } };
        var locked = Plan(game, open, library, locked: true);
        Assert.Equal(["wand"], locked.Start.Select(s => s.App.Id));
        // A command shell as a companion is no way out of the lock, whatever the rule says.
        Assert.Contains(locked.Skipped, s => s.AppId == "shell" && s.Reason.StartsWith("not-eligible"));
    }

    [Fact]
    public void What_the_person_may_not_start_does_not_start_as_a_companion()
    {
        // The gate is the person's own right to start the app (a child's list and time rules, areas, a protected profile).
        var wand = App("wand", @"C:\Wand\Wand.exe");
        var discord = App("discord", @"C:\Discord\Discord.exe");
        var game = Game("elden");
        var rules = new[] { new CompanionRule { AppId = "wand", Mode = "all" }, new CompanionRule { AppId = "discord", Mode = "all" } };
        var plan = Plan(game, rules, [wand, discord, game], gate: a => a.Id == "wand" ? "child" : null);
        Assert.Equal(["discord"], plan.Start.Select(s => s.App.Id));
        Assert.Contains(plan.Skipped, s => s is { AppId: "wand", Reason: "child" });
    }

    [Fact]
    public void A_start_target_that_moved_or_a_changed_start_is_judged_again_each_time()
    {
        var wand = App("wand", @"C:\Wand\Wand.exe");
        var game = Game("elden");
        var rules = new[] { new CompanionRule { AppId = "wand", Mode = "all" } };
        // The library's start target is gone: skipped instead of starting something else.
        var gone = Plan(game, rules, [wand, game], effective: _ => throw new InvalidOperationException("moved"));
        Assert.Empty(gone.Start);
        Assert.Equal("start", Assert.Single(gone.Skipped).Reason);
        // Swapped for a batch file or a shell after the rule was made: no longer eligible, not started.
        var swapped = Plan(game, rules, [wand, game], effective: _ => new LaunchTarget(LaunchKind.Exe, @"C:\Wand\x.cmd"));
        Assert.Empty(swapped.Start);
        // The app left the library.
        Assert.Equal("missing", Assert.Single(Plan(game, rules, [game]).Skipped).Reason);
    }

    [Fact]
    public void At_most_six_start_with_one_game()
    {
        var game = Game("elden");
        var apps = Enumerable.Range(0, 9).Select(i => App("a" + i, $@"C:\Apps\a{i}.exe")).ToList();
        var rules = apps.Select(a => new CompanionRule { AppId = a.Id, Mode = "all" }).Take(CompanionRules.MaxRules).ToList();
        var plan = Plan(game, rules, [.. apps, game]);
        Assert.Equal(CompanionRules.MaxStartsPerGame, plan.Start.Count);
        Assert.Equal(3, plan.Skipped.Count(s => s.Reason == "limit"));
    }

    [Fact]
    public void A_rule_carries_no_path_and_no_arguments()
    {
        // Pinned: a rule can only name a library app by id, so it can never start anything the library does not offer as an app.
        var names = typeof(CompanionRule).GetProperties().Select(p => p.Name).Order().ToList();
        // Sha256 is set by the host (the file that was checked), never by the page: it is not in CompanionChange.
        Assert.Equal(["AllowWritableFolder", "AppId", "CloseWithGame", "GameIds", "LockedSha256", "Minimized", "Mode", "Sha256", "WhenLocked"], names);
        var change = typeof(CompanionChange).GetProperties().Select(p => p.Name).Order().ToList();
        Assert.Equal(["CloseWithGame", "GameIds", "Minimized", "Mode", "WhenLocked"], change);
    }

    // ---- structural pins on the host side (source contracts)

    private static string Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx")))
                return File.ReadAllText(Path.Combine([dir.FullName, .. parts])).Replace("\r\n", "\n");
        throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void The_companion_start_never_asks_for_the_foreground_and_never_elevates()
    {
        // Code only: the comments say what is left out.
        var host = System.Text.RegularExpressions.Regex.Replace(Source("src", "CouchHud.Shell", "Services", "CompanionHost.cs"), @"//[^
]*", "");
        Assert.DoesNotContain("SetForegroundWindow", host);
        Assert.DoesNotContain("AttachThreadInput", host);
        Assert.DoesNotContain("runas", host);
        Assert.DoesNotContain("UseShellExecute", host);
        Assert.Contains("ShowMinNoActive", host);
        Assert.Contains("ShowNoActivate", host);
        // The start refuses a batch file or a Store app again where it runs.
        Assert.Contains("plan.Direct || plan.PackagedAppId is not null", host);
    }

    [Fact]
    public void Every_companion_decision_goes_through_the_one_plan_and_the_gate()
    {
        var hud = Source("src", "CouchHud.Shell", "HudCompanions.cs");
        // Planned twice (at the game's start and again after the wait for the foreground), each time with the person's own gate and the lock.
        // Planned three times: at the game's start, again after the wait for the foreground, and before every single start.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(hud, System.Text.RegularExpressions.Regex.Escape("CompanionPlanFor(session, OpenAtLaunch(session))")).Count);
        Assert.Contains("CompanionLauncher.StartAsync(plan, session.Item.Id, host, Environment.ProcessId, start =>", hud);
        Assert.Contains("CompanionRules.Plan(session.Item, rules, library.Find, app => library.EffectiveLaunch(app), CompanionGate,", hud);
        Assert.Contains("Locked && !openAtLaunch", hud);
        Assert.Contains("config.Fault is not null || ProfileHeld || !Areas.Runs(\"companionStart\")", hud);
        // The gate: areas, the child's list and age rules, and a start that would ask for a code or only advise is no start.
        Assert.Contains("FavoritesShelf.ChildAllows(rules, app, library.Items, null)", hud);
        Assert.Contains("check is { Ok: true, Advisory: false, UsesCode: false }", hud);
        // Rules are changed behind the unlock, a protected profile's choice and the parents' code.
        var set = hud[hud.IndexOf("public object SetCompanion(", StringComparison.Ordinal)..hud.IndexOf("public object RemoveCompanion(", StringComparison.Ordinal)];
        Assert.Contains("RequireProfileChosen();", set);
        Assert.Contains("RequireUnlocked();", set);
        Assert.Contains("RequireParentGrant(\"companions\", appId)", set);
        Assert.Contains("CompanionRules.Eligible(app, start, WindowsFolder) != CompanionVerdict.Ok", set);
        // The code is used up only after every check passed.
        Assert.True(set.IndexOf("RequireParentGrant", StringComparison.Ordinal) > set.IndexOf("CompanionRules.Eligible", StringComparison.Ordinal));
        Assert.True(set.IndexOf("RequireParentGrant", StringComparison.Ordinal) > set.IndexOf("companion.badGame", StringComparison.Ordinal));
        // Ending uses the identity-checked path only.
        Assert.Contains("CompanionStop.StopAsync(close, Win32ProcessControl.Instance", hud);
        Assert.DoesNotContain("Process.Kill", hud);
        Assert.DoesNotContain(".Kill(", hud);
    }

    [Fact]
    public void The_review_fixes_are_pinned_in_the_host_code()
    {
        var host = Source("src", "CouchHud.Shell", "Services", "CompanionHost.cs");
        // M2: the identity of the new process is read from the process handle before the handles are closed.
        var times = host.IndexOf("GetProcessTimes(process.hProcess", StringComparison.Ordinal);
        var image = host.IndexOf("QueryFullProcessImageName(process.hProcess", StringComparison.Ordinal);
        var closing = host.IndexOf("CloseHandle(process.hProcess)", StringComparison.Ordinal);
        Assert.True(times > 0 && image > 0 && closing > times && closing > image);
        Assert.Contains("OwnLaunches.Register(process.dwProcessId, startTime)", host);
        // M1: the file is held and judged on its real path; CreateProcess gets that path.
        Assert.Contains("CompanionPath.Hold(plan.FileName)", host);
        Assert.Contains("CreateProcessW(held.Path,", host);
        // M3: the focus guard asks for the companion's own tree and for the exemptions.
        Assert.Contains("CompanionFocus.Exempt(", host);
        Assert.Contains("OwnLaunches.DescendsFrom(pid, p => roots.TryGetValue(p, out var start) && start > 0 && Win32ProcessControl.StartOf(p) == start)", host);
        // M5: the ending handle also compares the program image.
        var control = Source("src", "CouchHud.Shell", "Services", "Win32ProcessControl.cs");
        var kill = control[control.IndexOf("public bool Kill(int pid, long start, string? image)", StringComparison.Ordinal)..];
        Assert.True(kill.IndexOf("QueryFullProcessImageName(handle", StringComparison.Ordinal) is > 0 && kill.IndexOf("QueryFullProcessImageName(handle", StringComparison.Ordinal) < kill.IndexOf("TerminateProcess(handle", StringComparison.Ordinal));
        var hud = Source("src", "CouchHud.Shell", "HudCompanions.cs");
        // M4: Wand is set up only for a trusted signature by Wand or WeMod; the unverified route adds a plain app only.
        Assert.Contains("held is not null && Authenticode.VerifyPublisher(held) ? (true, held.Sha256Hex()) : (false, null)", hud);
        Assert.Contains("if (!verified && !acceptUnverified) throw", hud);
        Assert.Contains("if (!verified) return GetCompanions();", hud);
        // L1: the gate is asked before every start. L7: names go through the plugin log sanitiser. L8: the hint waits for the unlock.
        Assert.Contains("start =>\n                Application.Current.Dispatcher.Invoke(() => CompanionPlanFor(session, OpenAtLaunch(session))", hud);
        Assert.DoesNotContain("{session.Item.Name}", hud);
        Assert.DoesNotContain("{skip.AppId}", hud);
        Assert.Contains("if (Locked || !WandSetup.HintDue(", hud);
        // L2: the open-couch note is made only after the start itself.
        var main = Source("src", "CouchHud.Shell", "Hud.cs");
        Assert.True(main.IndexOf("Launcher.Launch(item, start, LaunchOverrides.PinFor(start, own));", StringComparison.Ordinal)
            < main.IndexOf("NoteOpenLaunch(item, launchSerial, openCouch);", StringComparison.Ordinal));
        // The note is made after ForgetUnlock (which ends an earlier launch's note) and from the lock state read before it.
        Assert.True(main.IndexOf("var openCouch = !Locked;", StringComparison.Ordinal) < main.IndexOf("ForgetUnlock();\n        NoteOpenLaunch(item, launchSerial, openCouch);", StringComparison.Ordinal));
        // The Wand signature is checked with WinVerifyTrust, with no network lookup.
        var auth = Source("src", "CouchHud.Shell", "Services", "Authenticode.cs");
        Assert.Contains("WinVerifyTrust", auth);
        Assert.Contains("0x10 | 0x1000", auth);
        // The signer comes from the verification state of the held file, not from reading the file again; the state is closed in a finally.
        Assert.Contains("hFile = held.Stream.SafeFileHandle.DangerousGetHandle()", auth);
        Assert.Contains("WTHelperGetProvSignerFromChain", auth);
        Assert.DoesNotContain("CreateFromSignedFile", auth);
        Assert.True(auth.LastIndexOf("StateClose", StringComparison.Ordinal) > auth.IndexOf("finally", StringComparison.Ordinal));
        Assert.True(auth.IndexOf("DestroyStructure", StringComparison.Ordinal) < auth.IndexOf("FreeHGlobal", StringComparison.Ordinal));
        // The machine's roots only (never the user's store), and the held file is hashed and re-verified at the start.
        Assert.Contains("StoreLocation.LocalMachine", auth);
        Assert.DoesNotContain("StoreLocation.CurrentUser", auth);
        Assert.Contains("SignedProgram.Decide(start.Rule.Sha256, held.Sha256Hex(), () => Authenticode.VerifyPublisher(held))", host);
    }

    [Fact]
    public void The_bridge_calls_belong_to_gaming_or_apps_and_stay_closed_on_a_locked_couch()
    {
        foreach (var call in new[] { "getCompanions", "setCompanion", "removeCompanion", "confirmCompanion", "setupWand" })
        {
            Assert.Equal(FeatureAreas.Games, FeatureAreas.BridgeMethods[call]);
            Assert.DoesNotContain(call, LockPolicy.OpenWhileLocked);
        }
        Assert.Equal(FeatureAreas.Apps, FeatureAreas.BridgeMethods["getGuideApps"]);
        Assert.Equal(FeatureAreas.Apps, FeatureAreas.BridgeMethods["setGuideApp"]);
        // Reading the Guide menu's list is open (it answers nothing while locked); changing it is not.
        Assert.Contains("getGuideApps", LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("setGuideApp", LockPolicy.OpenWhileLocked);
        Assert.Equal(FeatureAreas.Games, FeatureAreas.Services["companionStart"]);
        // Gaming off: the companions are off with it.
        var off = FeatureAreas.From(new AppConfig { DisabledAreas = [FeatureAreas.Games] });
        Assert.False(off.Runs("companionStart"));
        Assert.False(off.AllowsMethod("setCompanion"));
        Assert.True(off.AllowsMethod("setGuideApp"));
    }

    [Fact]
    public void Companions_are_not_reachable_from_the_phone_the_assistant_or_plugins()
    {
        // Rules are the HUD page's business only (unlock + parents' code on the TV): none of these calls is in the remote's or the tools' lists.
        foreach (var file in new[] { new[] { "src", "CouchHud.Shell", "Services", "HudTools.cs" }, new[] { "src", "CouchHud.Shell", "Services", "RemoteHost.cs" } })
        {
            var text = Source(file);
            foreach (var call in new[] { "SetCompanion", "RemoveCompanion", "ConfirmCompanion", "confirmCompanion", "SetupWand", "SetGuideApp", "setCompanion", "setupWand", "setGuideApp" })
                Assert.DoesNotContain(call, text);
        }
    }
}
