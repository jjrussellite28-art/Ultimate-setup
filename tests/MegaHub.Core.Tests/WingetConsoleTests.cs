using System.Text;
using MegaHub.Core.Updates;

namespace MegaHub.Core.Tests;

/// <summary>winget under a pseudo console: the VT screen, the path of winget.exe, the command line, the id check and the step aside for a prompt.</summary>
public class WingetConsoleTests
{
    private const string Esc = "\x1B";

    // What conhost sends for a pseudo console, in its usual shapes: hidden cursor, the package line, a url, then the bar redrawn in place
    // by cursor positioning, blanks as "cursor forward", only the changed digits repainted, erase to the end of the line, and the taskbar
    // progress as OSC 9;4.
    private static readonly string ConPtyRun =
        Esc + "[?25l" + Esc + "[2J" + Esc + "[m" + Esc + "[H" + Esc + "]0;C:\\Windows\\System32\\winget.exe\x07" + Esc + "[?25h" +
        "(1/2) Found Mozilla Firefox (x64 de) [Mozilla.Firefox.de] Version 131.0.2\r\n" +
        "Downloading https://download-installer.cdn.mozilla.net/pub/firefox/releases/131.0.2/win64/de/Firefox%20Setup%20131.0.2.msi\r\n" +
        Esc + "[2C██▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒" + Esc + "[2C1.00 MB / 10.0 MB" + Esc + "[K" + Esc + "]9;4;1;10\x07" +
        Esc + "[3;3H███████████████" + Esc + "[3;35H5" + Esc + "]9;4;1;50\x07" +
        Esc + "[3;3H██████████████████████████████" + Esc + "[3;35H10.0" + Esc + "]9;4;1;100\x07\r\n" +
        "Successfully verified installer hash\r\n" +
        "Starting package install...\r\n" +
        "(2/2) Found Discord [Discord.Discord] Version 1.0.9164\r\n" +
        "Downloading https://dl.discordapp.net/distro/app/stable/win/x64/1.0.9164/DiscordSetup.exe\r\n" +
        "  ███████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  3.50 MB / 14.0 MB" + Esc + "]9;4;1;25\x07";

    private static List<string> Screen(string text, int chunk)
    {
        var screen = new ConsoleScreen(200, 30);
        var lines = new List<string>();
        for (var i = 0; i < text.Length; i += chunk) lines.AddRange(screen.Feed(text.AsSpan(i, Math.Min(chunk, text.Length - i))));
        return lines;
    }

    private static List<WingetProgressInfo> Steps(string text, int chunk)
    {
        var parser = new WingetProgressParser();
        var steps = new List<WingetProgressInfo>();
        foreach (var line in Screen(text, chunk))
            if (parser.Feed((line + "\n").AsSpan())) steps.Add(parser.Current!);
        if (parser.Flush()) steps.Add(parser.Current!);
        return steps;
    }

    [Fact]
    public void A_bar_redrawn_by_cursor_moves_reads_as_whole_lines()
    {
        var screen = new ConsoleScreen(200, 30);
        screen.Feed(ConPtyRun.AsSpan(0, ConPtyRun.IndexOf("Successfully", StringComparison.Ordinal)));
        Assert.Equal("(1/2) Found Mozilla Firefox (x64 de) [Mozilla.Firefox.de] Version 131.0.2", screen.Row(0));
        // Only "10.0" was repainted, the rest of the row stayed: the line is complete, not glued from pieces.
        Assert.Equal("  ██████████████████████████████  10.0 MB / 10.0 MB", screen.Row(2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(100000)]
    public void Under_a_pseudo_console_the_parser_gets_counter_bytes_and_percent(int chunk)
    {
        var steps = Steps(ConPtyRun, chunk);
        Assert.Equal(new WingetProgressInfo(1, 2, "Mozilla.Firefox.de", WingetPhase.Prepare, 0, 0, null), steps[0]);
        Assert.Contains(steps, s => s.Index == 1 && s.Phase == WingetPhase.Download && s.BytesDone == 1024 * 1024 && s.BytesTotal == 10 * 1024 * 1024);
        // The taskbar progress arrives as a percent.
        Assert.Contains(steps, s => s.Index == 1 && s.Percent is > 0.49 and < 0.51);
        Assert.Contains(steps, s => s.Index == 1 && s.Phase == WingetPhase.Install);
        var last = steps[^1];
        Assert.Equal((2, 2, "Discord.Discord"), (last.Index, last.Total, last.Id));
        Assert.Equal(WingetPhase.Download, last.Phase);
    }

    [Fact]
    public void Cursor_forward_is_blanks_and_erase_clears()
    {
        var screen = new ConsoleScreen(40, 5);
        screen.Feed("ab" + Esc + "[3Ccd" + Esc + "[1;4H" + Esc + "[K");
        Assert.Equal("ab", screen.Row(0));
        screen.Feed(Esc + "[2;1Hxyz" + Esc + "[2;2H" + Esc + "[1P");
        Assert.Equal("xz", screen.Row(1));
        screen.Feed(Esc + "[2;1H" + Esc + "[2@");
        Assert.Equal("  xz", screen.Row(1));
        screen.Feed(Esc + "[2J");
        Assert.Equal("", screen.Row(1));
    }

    [Fact]
    public void Wide_characters_take_two_cells_and_are_not_split_over_rows()
    {
        var screen = new ConsoleScreen(20, 5);
        // Two CJK characters take four cells: the cursor stands on column 5 afterwards, like in the console.
        screen.Feed("日本" + Esc + "[5Gx");
        Assert.Equal("日本x", screen.Row(0));
        // An emoji (surrogate pair) takes its two cells too.
        screen.Feed(Esc + "[2;1H\U0001F600" + Esc + "[3Gy");
        Assert.Equal("\U0001F600y", screen.Row(1));
        // Overwriting the second half of a wide character clears the whole character.
        screen.Feed(Esc + "[1;2Hz");
        Assert.Equal(" z本x", screen.Row(0));
        // A wide character that does not fit in the last cell goes to the next row whole.
        screen.Feed(Esc + "[3;20H日");
        Assert.Equal("", screen.Row(2));
        Assert.Equal("日", screen.Row(3));
        Assert.True(ConsoleScreen.IsWide('가'));
        Assert.False(ConsoleScreen.IsWide('A'));
    }

    [Fact]
    public void Carriage_return_and_backspace_overwrite_like_the_redirected_output()
    {
        var screen = new ConsoleScreen(40, 5);
        var lines = screen.Feed("  - \b\b\\ \r50 %   \r60 %");
        Assert.Equal("60 %", screen.Row(0));
        Assert.Equal(["60 %"], lines);
    }

    [Fact]
    public void Rows_that_scroll_away_inside_one_piece_are_still_handed_out_in_order()
    {
        var screen = new ConsoleScreen(40, 3);
        var lines = screen.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive");
        Assert.Equal(["one", "two", "three", "four", "five"], lines);
        // Nothing changed: nothing again.
        Assert.Empty(screen.Feed(""));
    }

    [Fact]
    public void Long_lines_wrap_and_the_grid_never_grows()
    {
        var screen = new ConsoleScreen(20, 2);
        screen.Feed(new string('x', 25));
        Assert.Equal(new string('x', 20), screen.Row(0));
        Assert.Equal("xxxxx", screen.Row(1));
        screen.Feed(Esc + "[99999;99999H!" + Esc + "[999999999A" + Esc + "[0;0H?");
        Assert.Equal("?xxxxxxxxxxxxxxxxxxx", screen.Row(0));
        Assert.EndsWith("!", screen.Row(1));
    }

    [Theory]
    [InlineData("9;4;1;45", "45 %")]
    [InlineData("9;4;2;100", "100 %")]
    [InlineData("9;4;4;7", "7 %")]
    [InlineData("9;4;3;0", null)]
    [InlineData("9;4;0;0", null)]
    [InlineData("9;4;1;101", null)]
    [InlineData("9;4;1;-5", null)]
    [InlineData("9;4;1;1234", null)]
    [InlineData("0;9;4;1;45", null)]
    [InlineData("8;;https://evil/", null)]
    public void Only_a_wellformed_taskbar_progress_becomes_a_percent(string osc, string? expected)
    {
        foreach (var end in new[] { "\x07", Esc + "\\" })
        {
            var lines = new ConsoleScreen(40, 5).Feed(Esc + "]" + osc + end);
            if (expected is null) Assert.Empty(lines);
            else Assert.Equal([expected], lines);
        }
    }

    [Fact]
    public void Oversized_or_broken_sequences_cost_nothing_and_do_not_leak_text()
    {
        var screen = new ConsoleScreen(40, 5);
        var lines = screen.Feed(Esc + "]" + new string('a', 100000) + "\x07" + Esc + "[" + new string('9', 100000) + "m" + Esc + "P" + "secret" + Esc + "\\ok");
        Assert.Equal(["ok"], lines);
        // A sequence cut by CAN ends; the text after it is ordinary again.
        Assert.Equal(["okfine"], screen.Feed(Esc + "[12\u0018fine"));
    }

    [Fact]
    public void Control_characters_never_reach_a_line()
    {
        var screen = new ConsoleScreen(40, 5);
        var lines = screen.Feed("a\u0000b\u0007c\u007F\u009Bd");
        Assert.Equal(["abcd"], lines);
    }

    // -----------------------------------------------------------------------
    // winget.exe

    private const string Local = @"C:\Users\kid\AppData\Local";
    private const string Alias = Local + @"\Microsoft\WindowsApps\winget.exe";
    private static readonly string[] Roots = [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)"];

    [Fact]
    public void Without_path_the_alias_in_windowsapps_is_taken()
    {
        Assert.Equal(Alias, WingetLocator.Resolve(null, Local, Roots, p => p == Alias, p => p == Alias));
        Assert.Equal(Alias, WingetLocator.Resolve(@"C:\Windows\System32;C:\Windows", Local, Roots, p => p == Alias, p => p == Alias));
    }

    [Fact]
    public void A_plain_file_planted_in_windowsapps_is_not_taken()
    {
        Assert.Null(WingetLocator.Resolve(null, Local, Roots, p => p == Alias, _ => false));
    }

    [Fact]
    public void Path_entries_count_only_in_protected_folders()
    {
        const string package = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24_x64__8wekyb3d8bbwe";
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools\winget.exe", package + @"\winget.exe", Alias };
        Assert.Equal(package + @"\winget.exe", WingetLocator.Resolve(@"C:\Tools;" + package, Local, Roots, all.Contains, _ => false));
        // A user's own folder on PATH is passed over; then the alias.
        Assert.Equal(Alias, WingetLocator.Resolve(@"C:\Tools", Local, Roots, all.Contains, p => p == Alias));
        // "C:\Program Files Evil" is not below "C:\Program Files".
        Assert.Null(WingetLocator.Resolve(@"C:\Program Files Evil", Local, Roots, _ => true, _ => false));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(@".\bin")]
    [InlineData("bin")]
    [InlineData(@"C:")]
    [InlineData(@"C:Windows")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"C:\Windows\..\Temp")]
    [InlineData(@"C:\Program Files\.\..\Temp")]
    [InlineData(@"C:\Windows\file.txt:stream")]
    [InlineData("C:\\Win\"dows")]
    public void Relative_unc_and_odd_path_entries_are_never_looked_at(string entry)
    {
        Assert.Empty(WingetLocator.Candidates(entry, null));
        Assert.Null(WingetLocator.Resolve(entry, null, Roots, _ => true, _ => true));
    }

    [Fact]
    public void Quoted_and_doubled_entries_are_cleaned_and_looked_at_once()
    {
        var dirs = WingetLocator.Candidates("\"C:\\Windows\";C:\\Windows\\;;  ;C:/Program Files", Local);
        Assert.Equal([@"C:\Windows", @"C:\Program Files", Local + @"\Microsoft\WindowsApps"], dirs);
    }

    [Fact]
    public void Nothing_found_is_null()
    {
        Assert.Null(WingetLocator.Resolve(@"C:\Windows", Local, Roots, _ => false, _ => true));
        Assert.Null(WingetLocator.Resolve(null, null, Roots, _ => true, _ => true));
    }

    // -----------------------------------------------------------------------
    // Command line

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("a&b|c", "a&b|c")]
    public void Arguments_are_quoted_the_way_windows_reads_them(string arg, string expected)
    {
        var sb = new StringBuilder();
        CommandLine.Append(sb, arg);
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void The_upgrade_line_keeps_each_argument_separate()
    {
        var line = CommandLine.Build(Alias, Winget.UpgradeArguments("Notepad++.Notepad++"));
        Assert.StartsWith("\"" + Alias + "\" upgrade --id Notepad++.Notepad++ --exact", line);
        Assert.Throws<ArgumentException>(() => CommandLine.Build("C:\\a\"b.exe", []));
        Assert.Throws<ArgumentException>(() => CommandLine.Build(Alias, ["a\0b"]));
    }

    // -----------------------------------------------------------------------
    // Package ids

    [Theory]
    [InlineData("Discord.Discord")]
    [InlineData("Notepad++.Notepad++")]
    [InlineData("9NBLGGH4NNS1")]
    [InlineData(@"ARP\Machine\X64\{8A69D345-D564-463C-AFF1-A69D9E530F96}")]
    [InlineData("Some_Vendor.App-Name~2")]
    public void Real_ids_pass(string id) => Assert.True(Winget.IsValidId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--source")]
    [InlineData("-x")]
    [InlineData("a b")]
    [InlineData("a\"b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a;b")]
    [InlineData("a|b")]
    [InlineData("a%PATH%")]
    [InlineData("a`b")]
    [InlineData("Ünicode.App")]
    [InlineData("a\u202Eb")]
    public void Odd_ids_are_refused(string? id)
    {
        Assert.False(Winget.IsValidId(id));
        if (id is not null) Assert.Throws<ArgumentException>(() => Winget.UpgradeArguments(id));
    }

    [Fact]
    public void Too_long_ids_are_refused() => Assert.False(Winget.IsValidId(new string('a', 129)));

    [Fact]
    public void The_table_drops_rows_with_ids_that_could_not_be_handed_back()
    {
        const string table =
            "Name      Id                 Version   Available Source\n" +
            "------------------------------------------------------\n" +
            "Good      Good.App           1.0       2.0       winget\n" +
            "Evil      --source=evil      1.0       2.0       winget\n" +
            "Quote     a\"b               1.0       2.0       winget\n";
        Assert.Equal(["Good.App"], Winget.ParseUpgrades(table).Select(i => i.Id));
    }

    // -----------------------------------------------------------------------
    // Stepping aside for a prompt

    [Fact]
    public void The_hud_steps_aside_for_the_prompt_and_comes_back_after_a_quiet_moment()
    {
        var yield = new UpgradeYield();
        Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(0, false, [])).Action);
        var step = yield.Next(new UpgradeScene(11, true, []));
        Assert.Equal((UpgradeYieldAction.Yield, 11L, true), (step.Action, step.Target, step.Prompt));
        // Same window: nothing new.
        Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(11, true, [])).Action);
        // The prompt is gone, the installer opens its window in the gap: the HUD stays aside for it.
        Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(0, false, [])).Action);
        step = yield.Next(new UpgradeScene(0, false, [22, 33]));
        Assert.Equal((UpgradeYieldAction.Yield, 22L, false), (step.Action, step.Target, step.Prompt));
        for (var i = 1; i < UpgradeYield.QuietTicks; i++) Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(0, false, [])).Action);
        Assert.Equal(UpgradeYieldAction.Restore, yield.Next(new UpgradeScene(0, false, [])).Action);
        Assert.False(yield.Yielded);
        Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(0, false, [])).Action);
    }

    [Fact]
    public void A_prompt_on_the_secure_desktop_needs_no_step_aside()
    {
        var yield = new UpgradeYield();
        // consent.exe runs, but its window is not on our desktop: nothing to bring forward, nothing to restore.
        Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(0, true, [])).Action);
        Assert.Equal(UpgradeYieldAction.None, yield.End(true, 0, UpgradeForeground.None).Action);
    }

    [Fact]
    public void The_end_of_the_run_brings_the_hud_back_only_if_it_stepped_aside()
    {
        var yield = new UpgradeYield();
        yield.Next(new UpgradeScene(0, false, [5]));
        Assert.Equal(UpgradeYieldAction.Restore, yield.End(true, 0, UpgradeForeground.None).Action);
        Assert.Equal(UpgradeYieldAction.None, yield.End(true, 0, UpgradeForeground.None).Action);
    }

    [Fact]
    public void No_step_aside_while_a_game_runs_only_a_note()
    {
        var yield = new UpgradeYield();
        // A game (or PC mode, the browser) owns the screen: the prompt and an installer window are not pushed over it.
        var step = yield.Next(new UpgradeScene(11, true, [22], HudOwnsScreen: false));
        Assert.Equal((UpgradeYieldAction.Note, 11L, true), (step.Action, step.Target, step.Prompt));
        Assert.Equal(UpgradeYieldAction.Note, yield.Next(new UpgradeScene(0, false, [22], HudOwnsScreen: false)).Action);
        Assert.False(yield.Yielded);
        // Nothing was stepped aside for, so nothing comes back.
        for (var i = 0; i < UpgradeYield.QuietTicks; i++) Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(0, false, [], HudOwnsScreen: false)).Action);
        Assert.Equal(UpgradeYieldAction.None, yield.End(false, 0, UpgradeForeground.None).Action);
    }

    [Fact]
    public void Release_when_the_hud_does_not_own_the_screen_at_the_end()
    {
        var yield = new UpgradeYield();
        Assert.Equal(UpgradeYieldAction.Yield, yield.Next(new UpgradeScene(0, false, [22])).Action);
        Assert.Equal(UpgradeYieldAction.Release, yield.End(false, 0, UpgradeForeground.None).Action);
    }

    [Fact]
    public void Someone_elses_window_in_front_is_a_note_not_a_yield()
    {
        var yield = new UpgradeYield();
        var step = yield.Next(new UpgradeScene(0, false, [22], Foreground: 99, ForegroundKind: UpgradeForeground.Other));
        Assert.Equal((UpgradeYieldAction.Note, 22L), (step.Action, step.Target));
        Assert.False(yield.Yielded);
        // The HUD, nothing or the upgrade's own window in front: stepping aside as before.
        Assert.Equal(UpgradeYieldAction.Yield, yield.Next(new UpgradeScene(0, false, [22], Foreground: 5, ForegroundKind: UpgradeForeground.Hud)).Action);
        // The act-time look: the front changed to someone else's window since the decision.
        Assert.Equal(UpgradeYieldAction.Note, yield.Recheck(new(UpgradeYieldAction.Yield, 22, false), 99, UpgradeForeground.Other).Action);
        Assert.Equal(UpgradeYieldAction.Release, yield.Recheck(new(UpgradeYieldAction.Restore, 0, false), 99, UpgradeForeground.Other).Action);
        Assert.Equal(UpgradeYieldAction.Restore, yield.Recheck(new(UpgradeYieldAction.Restore, 0, false), 22, UpgradeForeground.Other).Action);
        Assert.Equal(UpgradeYieldAction.Restore, yield.Recheck(new(UpgradeYieldAction.Restore, 0, false), 0, UpgradeForeground.None).Action);
    }

    [Fact]
    public void The_bare_desktop_counts_as_none_in_the_shell()
    {
        var watch = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MegaHub.Shell", "HudUpgradeWatch.cs"));
        foreach (var name in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }) Assert.Contains($"\"{name}\"", watch);
        Assert.Contains("return (0, UpgradeForeground.None);", watch);
    }

    [Fact]
    public void No_restore_when_someone_else_has_the_front()
    {
        var yield = new UpgradeYield();
        Assert.Equal(UpgradeYieldAction.Yield, yield.Next(new UpgradeScene(0, false, [22])).Action);
        // The installer is done, but the user started an app meanwhile: the HUD stays behind it.
        for (var i = 1; i < UpgradeYield.QuietTicks; i++) Assert.Equal(UpgradeYieldAction.None, yield.Next(new UpgradeScene(0, false, [], Foreground: 99, ForegroundKind: UpgradeForeground.Other)).Action);
        Assert.Equal(UpgradeYieldAction.Release, yield.Next(new UpgradeScene(0, false, [], Foreground: 99, ForegroundKind: UpgradeForeground.Other)).Action);
        Assert.False(yield.Yielded);
        // A game started meanwhile: no restore either.
        yield.Next(new UpgradeScene(0, false, [22]));
        Assert.Equal(UpgradeYieldAction.Release, yield.End(false, 0, UpgradeForeground.Hud).Action);
        // A window of this upgrade in front (one seen before, even if the caller cannot tell): back.
        yield.Next(new UpgradeScene(0, false, [22, 23]));
        Assert.Equal(UpgradeYieldAction.Restore, yield.End(true, 23, UpgradeForeground.Other).Action);
        yield.Next(new UpgradeScene(0, false, [22]));
        Assert.Equal(UpgradeYieldAction.Restore, yield.End(true, 5, UpgradeForeground.Hud).Action);
    }

    [Fact]
    public void The_target_stays_while_it_is_there_and_the_prompt_comes_first()
    {
        var yield = new UpgradeYield();
        Assert.Equal((UpgradeYieldAction.Yield, 22L), Pair(yield.Next(new UpgradeScene(0, false, [22, 33]))));
        // Another window gets bigger: the HUD does not jump to it while the first one is there.
        Assert.Equal((UpgradeYieldAction.None, 22L), Pair(yield.Next(new UpgradeScene(0, false, [33, 22]))));
        Assert.Equal((UpgradeYieldAction.None, 22L), Pair(yield.Next(new UpgradeScene(0, false, [33, 22]))));
        // The first one is gone: the next one.
        Assert.Equal((UpgradeYieldAction.Yield, 33L), Pair(yield.Next(new UpgradeScene(0, false, [33]))));
        // The prompt shows up: it comes first.
        Assert.Equal((UpgradeYieldAction.Yield, 11L), Pair(yield.Next(new UpgradeScene(11, true, [33]))));
        static (UpgradeYieldAction, long) Pair(UpgradeYieldStep s) => (s.Action, s.Target);
    }

    [Fact]
    public void The_shell_decides_with_the_screen_context_and_starts_the_mouse_only_on_an_open_couch()
    {
        var watch = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MegaHub.Shell", "HudUpgradeWatch.cs"));
        // The decision gets whether the HUD owns the screen and what is in front, on the UI thread.
        Assert.Contains("HudOwnsScreen(), look.Foreground, look.ForegroundKind);", watch);
        Assert.Contains("yield.Next(scene)", watch);
        Assert.Contains("yield.End(HudOwnsScreen(), fg, kind)", watch);
        Assert.Contains("return !(context.GameRunning || context.PcMode || context.Browsing || context.Suspended);", watch);
        // Yield and Restore ask once more at the moment they happen.
        var apply = watch[watch.IndexOf("private void ApplyUpgradeYield", StringComparison.Ordinal)..];
        var yieldCase = apply[apply.IndexOf("case UpgradeYieldAction.Yield:", StringComparison.Ordinal)..apply.IndexOf("case UpgradeYieldAction.Release:", StringComparison.Ordinal)];
        Assert.True(yieldCase.IndexOf("if (!HudOwnsScreen())", StringComparison.Ordinal) < yieldCase.IndexOf("ForceForeground", StringComparison.Ordinal));
        Assert.Contains("if (!step.Prompt && !Locked && _mouse is { Active: false } mouse)", yieldCase);
        var restoreCase = apply[apply.IndexOf("case UpgradeYieldAction.Restore:", StringComparison.Ordinal)..];
        Assert.True(restoreCase.IndexOf("if (!HudOwnsScreen())", StringComparison.Ordinal) < restoreCase.IndexOf("BringToFront", StringComparison.Ordinal));
        // The mouse the watch started ends on every way out (Stop, never the HUD-showing EndMouseMode).
        Assert.Contains("_mouse.Stop();", watch);
        Assert.DoesNotContain("EndMouseMode()", watch);
        // The output pump never hands out text after the run is over.
        var run = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MegaHub.Shell", "Services", "PseudoConsoleRun.cs"));
        Assert.Contains("if (Volatile.Read(ref closed) == 0) onOutput(text);", run);
        Assert.DoesNotContain("LongRunning", run);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MegaHub.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
