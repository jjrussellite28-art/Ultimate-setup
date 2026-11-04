using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Plugins without a restart: starting a folder now goes through the very same loader as the start (trust check on the held files, revocation,
/// the switch, the grant), a plugin is invisible until it has started and leaves nothing behind when it fails or hangs, stopping cuts it off
/// first and then asks it to stop with a time limit, and a plugin that cannot be stopped for sure is not started a second time. The plugins are
/// real assemblies built on the fly.
/// </summary>
public sealed class PluginHotLoadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("couchhud-hot-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A plugin that could not be stopped keeps its files until the test process ends.
        }
    }

    private string DataDir => Path.Combine(_root, "data");
    private string Plugins => Path.Combine(DataDir, "plugins");

    private static int _counter;

    [Flags]
    private enum Mode
    {
        None = 0,
        /// <summary>Implements IDisposable; Dispose writes the marker file.</summary>
        DisposeMarks = 1,
        DisposeHangs = 2,
        DisposeThrows = 4,
        InitThrowsAfterRegistering = 8,
        InitHangs = 16,
        /// <summary>Initialize sleeps 1.5 s after it registered its function (to look at the manager meanwhile).</summary>
        InitSlow = 32,
        /// <summary>Initialize subscribes to the language event of the host (a handler of the plugin hangs on a host object).</summary>
        SubscribesEvent = 64
    }

    private sealed record Built(string Folder, string Dir, string AssemblyName, string Marker)
    {
        /// <summary>Written by Initialize of an InitSlow plugin as its very last act before it returns.</summary>
        public string InitDone => Marker + ".initdone";
    }

    /// <summary>A plugin assembly: Initialize keeps the context in the static field Ctx and registers the function "fn"; the modes add the rest.</summary>
    private Built Build(string folder, string pluginId, Mode mode = Mode.None)
    {
        var name = "HotPlugin" + Interlocked.Increment(ref _counter);
        var dir = Path.Combine(Plugins, folder);
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(_root, folder + ".marker");
        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule(name);
        var disposable = (mode & (Mode.DisposeMarks | Mode.DisposeHangs | Mode.DisposeThrows)) != 0;
        var tb = mb.DefineType(name + ".P", TypeAttributes.Public | TypeAttributes.Class, typeof(object), disposable ? [typeof(IPlugin), typeof(IDisposable)] : [typeof(IPlugin)]);
        tb.DefineDefaultConstructor(MethodAttributes.Public);
        var ctxField = tb.DefineField("Ctx", typeof(IPluginContext), FieldAttributes.Public | FieldAttributes.Static);
        var onLang = tb.DefineMethod("OnLang", MethodAttributes.Public, typeof(void), [typeof(string)]);
        onLang.GetILGenerator().Emit(OpCodes.Ret);

        const MethodAttributes Virt = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final;
        var getter = tb.DefineMethod("get_Id", Virt | MethodAttributes.SpecialName, typeof(string), Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldstr, pluginId);
        il.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(getter, typeof(IPlugin).GetProperty("Id")!.GetMethod!);

        var init = tb.DefineMethod("Initialize", Virt, typeof(void), [typeof(IPluginContext)]);
        il = init.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stsfld, ctxField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, typeof(PluginFunction));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, "fn");
        il.Emit(OpCodes.Ldstr, "Funktion");
        il.Emit(OpCodes.Ldstr, "Function");
        il.Emit(OpCodes.Newobj, typeof(PluginFunction).GetConstructor([typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, typeof(IPluginContext).GetMethod("RegisterFunctions")!);
        if (mode.HasFlag(Mode.SubscribesEvent))
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, typeof(IPluginContext).GetProperty("Events")!.GetMethod!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldftn, onLang);
            il.Emit(OpCodes.Newobj, typeof(Action<string>).GetConstructor([typeof(object), typeof(IntPtr)])!);
            il.Emit(OpCodes.Callvirt, typeof(IPluginEvents).GetEvent("LanguageChanged")!.AddMethod!);
        }
        if (mode.HasFlag(Mode.InitSlow))
        {
            il.Emit(OpCodes.Ldc_I4, 1500);
            il.Emit(OpCodes.Call, typeof(Thread).GetMethod("Sleep", [typeof(int)])!);
            // The last act before Initialize returns: from here on the plugin may become visible.
            il.Emit(OpCodes.Ldstr, marker + ".initdone");
            il.Emit(OpCodes.Ldstr, "fertig");
            il.Emit(OpCodes.Call, typeof(File).GetMethod("WriteAllText", [typeof(string), typeof(string)])!);
        }
        if (mode.HasFlag(Mode.InitHangs))
        {
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Call, typeof(Thread).GetMethod("Sleep", [typeof(int)])!);
        }
        if (mode.HasFlag(Mode.InitThrowsAfterRegistering))
        {
            il.Emit(OpCodes.Ldstr, "kaputt");
            il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
            il.Emit(OpCodes.Throw);
        }
        il.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(init, typeof(IPlugin).GetMethod("Initialize")!);

        if (disposable)
        {
            var dispose = tb.DefineMethod("Dispose", Virt, typeof(void), Type.EmptyTypes);
            il = dispose.GetILGenerator();
            if (mode.HasFlag(Mode.DisposeHangs))
            {
                il.Emit(OpCodes.Ldc_I4_M1);
                il.Emit(OpCodes.Call, typeof(Thread).GetMethod("Sleep", [typeof(int)])!);
            }
            if (mode.HasFlag(Mode.DisposeThrows))
            {
                il.Emit(OpCodes.Ldstr, "nein");
                il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
                il.Emit(OpCodes.Throw);
            }
            if (mode.HasFlag(Mode.DisposeMarks))
            {
                il.Emit(OpCodes.Ldstr, marker);
                il.Emit(OpCodes.Ldstr, "gestoppt");
                il.Emit(OpCodes.Call, typeof(File).GetMethod("WriteAllText", [typeof(string), typeof(string)])!);
            }
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(dispose, typeof(IDisposable).GetMethod("Dispose")!);
        }
        tb.CreateType();
        ab.Save(Path.Combine(dir, folder + ".dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"id\":\"" + pluginId + "\",\"version\":\"1.0.0\"}");
        return new Built(folder, dir, name, marker);
    }

    private PluginTrust Trusted(Built plugin, PluginOrigin origin = PluginOrigin.ThirdParty, IReadOnlyList<string>? capabilities = null, string? pluginId = null)
    {
        var trust = new PluginTrust(DataDir);
        trust.Trust(plugin.Folder, plugin.Dir, new PluginTrustEntry { Hash = "", Origin = origin, PluginId = pluginId, Version = "1.0.0", Capabilities = capabilities ?? [] });
        return trust;
    }

    private PluginManager Manager(PluginTrust trust, Func<string, bool>? enabled = null, TimeSpan? initTimeout = null, TimeSpan? stopTimeout = null) =>
        new([(Plugins, true)], DataDir, enabled ?? (_ => true))
        {
            Trust = trust,
            FirstPartyIds = () => [],
            InitializeTimeout = initTimeout ?? TimeSpan.FromSeconds(10),
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5)
        };

    /// <summary>The Ctx field of the plugin that runs from this assembly name (the context the host gave it), found through the collectible load contexts.</summary>
    private static IPluginContext? CtxOf(Built plugin)
    {
        foreach (var context in AssemblyLoadContext.All)
        {
            var assembly = context.Assemblies.FirstOrDefault(a => a.GetName().Name == plugin.AssemblyName);
            if (assembly is null) continue;
            return (IPluginContext?)assembly.GetType(plugin.AssemblyName + ".P")!.GetField("Ctx")!.GetValue(null);
        }
        return null;
    }

    // ---- Starting now: the same checks as at the start ----

    [Fact]
    public async Task A_trusted_folder_starts_now_and_is_listed_like_one_that_started_with_CouchHUD()
    {
        var plugin = Build("hotone", "hotone");
        var trust = Trusted(plugin);
        var atStart = Manager(trust);
        atStart.LoadAll();
        var startGrant = atStart.GrantOf("hotone")!;
        await atStart.StopAsync("hotone");

        var manager = Manager(trust);
        var infos = await manager.LoadNowAsync("hotone");

        var info = Assert.Single(infos);
        Assert.True(info.Loaded);
        Assert.Equal("hotone", info.Id);
        Assert.Single(manager.Functions);
        var grant = manager.GrantOf("hotone")!;
        Assert.Equal(startGrant.Origin, grant.Origin);
        Assert.Equal(startGrant.Capabilities.Order(), grant.Capabilities.Order());
        Assert.True(manager.IsRunning("hotone"));
    }

    [Fact]
    public async Task Starting_now_runs_the_trust_check_on_the_files_a_changed_dll_does_not_load()
    {
        var plugin = Build("hotchanged", "hotchanged");
        var trust = Trusted(plugin);
        // Swapped after the approval, before the start: the held files do not match the entry.
        File.AppendAllText(Path.Combine(plugin.Dir, "hotchanged.dll"), "tampered");
        var manager = Manager(trust);

        var info = Assert.Single(await manager.LoadNowAsync("hotchanged"));

        Assert.False(info.Loaded);
        Assert.True(info.NeedsApproval);
        Assert.True(info.Changed);
        Assert.Empty(manager.Functions);
        Assert.Null(manager.GrantOf("hotchanged"));
        Assert.False(manager.IsRunning("hotchanged"));
    }

    [Fact]
    public async Task A_folder_nobody_approved_does_not_start_now_either()
    {
        var plugin = Build("hotunknown", "hotunknown");
        var manager = Manager(new PluginTrust(DataDir));

        var info = Assert.Single(await manager.LoadNowAsync("hotunknown"));

        Assert.False(info.Loaded);
        Assert.True(info.NeedsApproval);
        Assert.Empty(manager.Functions);
        _ = plugin;
    }

    [Fact]
    public async Task A_withdrawn_plugin_and_a_switched_off_one_do_not_start_now()
    {
        var plugin = Build("hotrevoked", "hotrevoked");
        var trust = Trusted(plugin, PluginOrigin.ThirdParty, [], "hotrevoked");
        var revoked = Manager(trust);
        revoked.Revocations = [new CatalogRevocation("hotrevoked", [], null, "danger")];
        var info = Assert.Single(await revoked.LoadNowAsync("hotrevoked"));
        Assert.True(info.Revoked);
        Assert.False(info.Loaded);
        Assert.Empty(revoked.Functions);

        var off = Manager(trust, enabled: _ => false);
        var offInfo = Assert.Single(await off.LoadNowAsync("hotrevoked"));
        Assert.False(offInfo.Loaded);
        Assert.False(offInfo.Enabled);
        Assert.Empty(off.Functions);
    }

    [Fact]
    public async Task A_removed_folder_never_starts()
    {
        var plugin = Build("hotremoved", "hotremoved");
        var trust = Trusted(plugin);
        trust.MarkRemoved("hotremoved");
        var manager = Manager(trust);

        Assert.Empty(await manager.LoadNowAsync("hotremoved"));
        Assert.Empty(manager.Functions);
    }

    [Fact]
    public async Task A_reserved_id_in_the_code_is_refused_at_a_start_by_hand_as_at_the_start()
    {
        var plugin = Build("hotreserved", "homeassistant");
        var trust = Trusted(plugin, PluginOrigin.Local);
        var manager = Manager(trust);

        var info = Assert.Single(await manager.LoadNowAsync("hotreserved"));

        Assert.False(info.Loaded);
        Assert.Equal(Strings.Get("plugin.idMismatch", "homeassistant"), info.Error);
        Assert.Null(manager.GrantOf("homeassistant"));
    }

    [Fact]
    public async Task Starting_a_folder_that_runs_does_nothing_twice()
    {
        var plugin = Build("hottwice", "hottwice");
        var manager = Manager(Trusted(plugin));

        await manager.LoadNowAsync("hottwice");
        var again = await manager.LoadNowAsync("hottwice");

        Assert.Single(again);
        Assert.Single(manager.Functions);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_an_error_not_a_crash()
    {
        var manager = Manager(new PluginTrust(DataDir));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.LoadNowAsync("nothere"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.LoadNowAsync(".."));
    }

    // ---- No half-loaded plugin ----

    [Fact]
    public async Task A_plugin_that_is_still_starting_is_invisible_to_every_list()
    {
        var plugin = Build("hotslow", "hotslow", Mode.InitSlow);
        var manager = Manager(Trusted(plugin), initTimeout: TimeSpan.FromSeconds(60));

        var start = Task.Run(() => manager.LoadNowAsync("hotslow"));
        var seen = false;
        var looked = 0;
        var watch = Stopwatch.StartNew();
        // Only until the plugin says its Initialize is about to return (the marker file is its last act): before that nothing may be visible,
        // however slow or loaded the machine is. After it the commit may happen at any moment.
        while (!File.Exists(plugin.InitDone) && watch.Elapsed < TimeSpan.FromSeconds(50))
        {
            looked++;
            var visible = manager.Functions.Count > 0 || manager.Plugins.Any(p => p.Loaded) || manager.Pages.Count > 0 || manager.ToolSpecs.Count > 0;
            // The marker is looked at after the lists: if it is still missing then, the lists were read before the plugin's last act, so
            // before any commit (a thread that was paused between the two reads cannot make this fail).
            if (visible && !File.Exists(plugin.InitDone)) seen = true;
            await Task.Delay(5);
        }
        var infos = await start;

        Assert.True(looked > 0 || File.Exists(plugin.InitDone));
        Assert.False(seen, "the plugin was visible before its start finished");
        Assert.True(Assert.Single(infos).Loaded);
        Assert.Single(manager.Functions);
    }

    [Fact]
    public async Task A_plugin_that_fails_to_start_leaves_nothing_behind()
    {
        var plugin = Build("hotfail", "hotfail", Mode.InitThrowsAfterRegistering);
        var manager = Manager(Trusted(plugin));

        var info = Assert.Single(await manager.LoadNowAsync("hotfail"));

        Assert.False(info.Loaded);
        Assert.Equal("kaputt", info.Error);
        // It registered a function before it threw: gone again, with its grant.
        Assert.Empty(manager.Functions);
        Assert.Null(manager.GrantOf("hotfail"));
        Assert.False(manager.IsRunning("hotfail"));
        // And what it kept of its context stays cut off.
        CtxOf(plugin)?.RegisterFunctions(new PluginFunction("later", "Spaet", "Late"));
        Assert.Empty(manager.Functions);
    }

    [Fact]
    public async Task A_plugin_whose_start_hangs_is_cut_off_after_the_timeout_and_blocks_nothing()
    {
        var plugin = Build("hothang", "hothang", Mode.InitHangs);
        var manager = Manager(Trusted(plugin), initTimeout: TimeSpan.FromMilliseconds(400));
        var watch = Stopwatch.StartNew();

        var info = Assert.Single(await manager.LoadNowAsync("hothang"));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString());
        Assert.False(info.Loaded);
        Assert.Equal(Strings.Get("plugin.initTimeout", "hothang"), info.Error);
        Assert.Empty(manager.Functions);
        Assert.Null(manager.GrantOf("hothang"));
        // The next plugin loads fine (the lifecycle lock was released).
        var other = Build("hotafter", "hotafter");
        var second = new PluginTrust(DataDir);
        second.Trust("hotafter", other.Dir, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, Capabilities = [] });
        Assert.True(Assert.Single(await manager.LoadNowAsync("hotafter")).Loaded);
    }

    // ---- Stopping ----

    [Fact]
    public async Task Stopping_cuts_the_plugin_off_asks_it_to_stop_and_lets_go_of_the_files()
    {
        var plugin = Build("hotstop", "hotstop", Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotstop");
        var ctx = CtxOf(plugin)!;
        var raised = 0;
        manager.TabsChanged += () => raised++;

        var result = await manager.StopAsync("hotstop");

        Assert.True(result.WasRunning);
        Assert.Equal(PluginStop.Clean, result.Stop);
        Assert.True(result.FullyStopped);
        Assert.True(File.Exists(plugin.Marker));
        Assert.Empty(manager.Functions);
        Assert.Null(manager.GrantOf("hotstop"));
        Assert.False(manager.IsRunning("hotstop"));
        Assert.False(Assert.Single(manager.Plugins).Loaded);
        Assert.False(Assert.Single(manager.Plugins).StopsAfterRestart);
        Assert.True(raised > 0);
        // A leftover thread of the old plugin reaches nothing.
        ctx.RegisterFunctions(new PluginFunction("late", "Spaet", "Late"));
        Assert.Empty(manager.Functions);
        // Files and load context are let go: the folder can be moved away at once.
        Assert.True(result.Collected, "the load context was not collected");
        Directory.Move(plugin.Dir, plugin.Dir + "-moved");
    }

    [Fact]
    public async Task A_stopped_plugin_gets_no_more_events()
    {
        var plugin = Build("hotevents", "hotevents", Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotevents");
        var ctx = CtxOf(plugin)!;
        var called = 0;
        ctx.Events.LanguageChanged += _ => Interlocked.Increment(ref called);
        manager.Events.RaiseLanguageChanged("en");
        await Task.Delay(200);
        Assert.Equal(1, called);

        await manager.StopAsync("hotevents");
        manager.Events.RaiseLanguageChanged("de");
        await Task.Delay(200);

        Assert.Equal(1, called);
    }

    [Fact]
    public async Task A_plugin_without_a_way_to_be_told_is_cut_off_but_not_started_a_second_time()
    {
        var plugin = Build("hotnodispose", "hotnodispose");
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotnodispose");

        var result = await manager.StopAsync("hotnodispose");

        Assert.Equal(PluginStop.NoDispose, result.Stop);
        Assert.False(result.FullyStopped);
        Assert.Empty(manager.Functions);
        Assert.True(Assert.Single(manager.Plugins).StopsAfterRestart);
        Assert.Equal(PluginStop.NoDispose, manager.UnstoppedRun("hotnodispose"));
        // Switching it on again: not started, the restart does it (two copies of one plugin never run side by side).
        var again = Assert.Single(await manager.LoadNowAsync("hotnodispose"));
        Assert.False(again.Loaded);
        Assert.True(again.NeedsRestartToStart);
        Assert.True(manager.Plugins.Single().StartsAfterRestart);
        Assert.Empty(manager.Functions);
        Assert.False(manager.IsRunning("hotnodispose"));
    }

    [Fact]
    public async Task An_uninstalled_plugin_whose_files_stay_is_listed_as_removed_at_the_next_start_and_never_starts_again()
    {
        var plugin = Build("hotremoved", "hotremoved");
        var trust = Trusted(plugin);
        var manager = Manager(trust);
        await manager.LoadNowAsync("hotremoved");
        // The same order as the host: the removal is marked first, then the plugin is cut off and asked to stop.
        trust.MarkRemoved("hotremoved");
        var result = await manager.StopAsync("hotremoved");
        Assert.False(result.FullyStopped);

        var listed = Assert.Single(manager.Plugins);
        Assert.True(listed.RemovedAtRestart);
        Assert.False(listed.Loaded);
        Assert.False(listed.StartsAfterRestart);
        Assert.True(manager.IsUninstalled("hotremoved"));

        // Switching it on, approving it or accepting it again: nothing starts, nothing is listed as "starts after restart", no second copy.
        var again = Assert.Single(await manager.LoadNowAsync("hotremoved"));
        Assert.True(again.RemovedAtRestart);
        Assert.False(again.Loaded);
        Assert.False(again.NeedsRestartToStart);
        Assert.False(manager.IsRunning("hotremoved"));
        Assert.Empty(manager.Functions);
        Assert.Null(manager.GrantOf("hotremoved"));

        // A start of CouchHUD (here: LoadAll) does not list or load it either.
        var restarted = Manager(trust);
        restarted.LoadAll();
        var relisted = Assert.Single(restarted.Plugins, p => p.Folder == "hotremoved");
        Assert.True(relisted.RemovedAtRestart);
        Assert.False(relisted.Loaded);
        Assert.False(relisted.Enabled);
        Assert.False(restarted.IsRunning("hotremoved"));
        Assert.Empty(restarted.Functions);
    }

    [Fact]
    public async Task A_plugin_whose_folder_is_gone_is_dropped_from_the_list()
    {
        var plugin = Build("hotdropped", "hotdropped", Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotdropped");
        var trust = new PluginTrust(DataDir);
        trust.MarkRemoved("hotdropped");
        var result = await manager.StopAsync("hotdropped");
        Assert.True(result.Collected, "the files must be free");
        Assert.True(Assert.Single(manager.Plugins).RemovedAtRestart);

        // The folder goes (what the uninstall does once the files are free), the mark with it; the listing is dropped.
        Directory.Move(plugin.Dir, plugin.Dir + "-gone");
        trust.ClearRemoved("hotdropped");
        manager.DropListing("hotdropped");

        Assert.Empty(manager.Plugins);
        Assert.False(manager.IsUninstalled("hotdropped"));
    }

    [Fact]
    public async Task Approving_accepting_or_recording_a_marked_folder_is_refused_and_the_mark_stays()
    {
        var plugin = Build("hotmarked", "hotmarked");
        var trust = Trusted(plugin);
        var manager = Manager(trust);
        await manager.LoadNowAsync("hotmarked");
        trust.MarkRemoved("hotmarked");
        await manager.StopAsync("hotmarked");
        var shown = PluginTrust.HashFolder(plugin.Dir);
        var refused = Strings.Get("plugin.removedPending");

        Assert.Equal(refused, Assert.Throws<InvalidOperationException>(() => manager.Approve("hotmarked", shown)).Message);
        Assert.Equal(refused, Assert.Throws<InvalidOperationException>(() => manager.ApproveInstalled("hotmarked", shown)).Message);
        Assert.Equal(refused, Assert.Throws<InvalidOperationException>(() => manager.AcceptRevocation("hotmarked")).Message);

        Assert.True(new PluginTrust(DataDir).IsRemoved("hotmarked"));
        Assert.Null(new PluginTrust(DataDir).Entry("hotmarked"));
        Assert.True(manager.Plugins.Single().RemovedAtRestart);
        Assert.False(manager.IsRunning("hotmarked"));
    }

    [Fact]
    public async Task A_plugin_that_hangs_while_stopping_does_not_block_CouchHUD_and_stays_cut_off()
    {
        var plugin = Build("hotdisposehang", "hotdisposehang", Mode.DisposeHangs);
        var manager = Manager(Trusted(plugin), stopTimeout: TimeSpan.FromMilliseconds(400));
        await manager.LoadNowAsync("hotdisposehang");
        var watch = Stopwatch.StartNew();

        var result = await manager.StopAsync("hotdisposehang");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString());
        Assert.Equal(PluginStop.TimedOut, result.Stop);
        Assert.Empty(manager.Functions);
        Assert.Null(manager.GrantOf("hotdisposehang"));
        Assert.Equal(PluginStop.TimedOut, manager.UnstoppedRun("hotdisposehang"));
        // The lifecycle lock is free again: other plugins can be handled.
        var other = Build("hotother", "hotother");
        Trusted(other);
        Assert.True(Assert.Single(await manager.LoadNowAsync("hotother")).Loaded);
    }

    [Fact]
    public async Task A_plugin_that_throws_while_stopping_counts_as_not_stopped()
    {
        var plugin = Build("hotdisposethrow", "hotdisposethrow", Mode.DisposeThrows);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotdisposethrow");

        var result = await manager.StopAsync("hotdisposethrow");

        Assert.Equal(PluginStop.Failed, result.Stop);
        Assert.Empty(manager.Functions);
        Assert.True(Assert.Single(manager.Plugins).StopsAfterRestart);
    }

    [Fact]
    public async Task After_a_clean_stop_the_same_folder_starts_again_as_a_new_instance_and_the_old_context_stays_dead()
    {
        var plugin = Build("hotagain", "hotagain", Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotagain");
        var oldCtx = CtxOf(plugin)!;
        await manager.StopAsync("hotagain");

        var info = Assert.Single(await manager.LoadNowAsync("hotagain"));

        Assert.True(info.Loaded);
        Assert.Single(manager.Functions);
        Assert.NotNull(manager.GrantOf("hotagain"));
        // The old instance's context is still cut off: it cannot add to the new one.
        oldCtx.RegisterFunctions(new PluginFunction("ghost", "Geist", "Ghost"));
        Assert.Single(manager.Functions);
        Assert.False(manager.HasFunction("hotagain", "ghost"));
    }

    [Fact]
    public async Task Stopping_something_that_never_ran_is_a_clean_nothing()
    {
        var manager = Manager(new PluginTrust(DataDir));
        var result = await manager.StopAsync("nothing");
        Assert.False(result.WasRunning);
        Assert.True(result.FullyStopped);
    }

    [Fact]
    public async Task The_files_of_a_running_plugin_cannot_be_swapped_or_deleted_and_a_new_file_is_not_part_of_it()
    {
        var plugin = Build("hotheld", "hotheld", Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotheld");
        var dll = Path.Combine(plugin.Dir, "hotheld.dll");

        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(dll, [1, 2, 3]));
        Assert.ThrowsAny<IOException>(() => File.Delete(dll));
        Assert.ThrowsAny<IOException>(() => Directory.Move(plugin.Dir, plugin.Dir + "-x"));
        await manager.StopAsync("hotheld");
    }

    // ---- The snapshot ----

    [Fact]
    public void The_snapshot_hash_is_the_hash_the_trust_list_records()
    {
        var plugin = Build("hotsnap", "hotsnap");
        Directory.CreateDirectory(Path.Combine(plugin.Dir, "sub"));
        File.WriteAllText(Path.Combine(plugin.Dir, "sub", "data.txt"), "x");
        File.WriteAllText(Path.Combine(plugin.Dir, "thumbs.db"), "ignored");

        using var snapshot = PluginFolderSnapshot.Open(plugin.Dir);

        Assert.Equal(PluginTrust.HashFolder(plugin.Dir), snapshot.Hash);
        Assert.True(snapshot.Contains(Path.Combine(plugin.Dir, "hotsnap.dll")));
        Assert.True(snapshot.Contains(Path.Combine(plugin.Dir, "sub", "data.txt")));
        // Not hashed, so not part of it.
        Assert.False(snapshot.Contains(Path.Combine(plugin.Dir, "thumbs.db")));
        Assert.False(snapshot.Contains(Path.Combine(plugin.Dir, "nothere.dll")));
    }

    [Fact]
    public void A_file_that_appears_after_the_hash_is_not_part_of_the_snapshot_and_the_snapshot_holds_what_it_hashed()
    {
        var plugin = Build("hotsnap2", "hotsnap2");
        using var snapshot = PluginFolderSnapshot.Open(plugin.Dir);
        var late = Path.Combine(plugin.Dir, "Late.dll");
        File.WriteAllBytes(late, [0x4d, 0x5a]);

        Assert.False(snapshot.Contains(late));
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(Path.Combine(plugin.Dir, "plugin.json"), [1]));
        Assert.ThrowsAny<IOException>(() => File.Delete(Path.Combine(plugin.Dir, "hotsnap2.dll")));

        snapshot.Dispose();
        Assert.False(snapshot.Contains(Path.Combine(plugin.Dir, "hotsnap2.dll")));
        File.WriteAllBytes(Path.Combine(plugin.Dir, "plugin.json"), [1]);
    }

    [Fact]
    public void A_file_that_is_being_written_cannot_be_held_so_nothing_is_judged()
    {
        var plugin = Build("hotbusy", "hotbusy");
        using var writer = new FileStream(Path.Combine(plugin.Dir, "hotbusy.dll"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Assert.ThrowsAny<IOException>(() => PluginFolderSnapshot.Open(plugin.Dir));
    }

    [Fact]
    public async Task A_folder_with_a_file_being_written_does_not_load_and_is_not_offered_as_trusted()
    {
        var plugin = Build("hotbusy2", "hotbusy2");
        var trust = Trusted(plugin);
        using var writer = new FileStream(Path.Combine(plugin.Dir, "hotbusy2.dll"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var manager = Manager(trust);

        var info = Assert.Single(await manager.LoadNowAsync("hotbusy2"));

        Assert.False(info.Loaded);
        Assert.Equal(Strings.Get("plugin.filesBusy"), info.Error);
        Assert.Empty(manager.Functions);
    }

    [Fact]
    public void The_load_context_loads_nothing_that_was_not_hashed()
    {
        var plugin = Build("hotctx", "hotctx");
        using var snapshot = PluginFolderSnapshot.Open(plugin.Dir);
        // A library dropped in after the check: the resolver finds it next to the plugin, the context refuses it.
        var late = Path.Combine(plugin.Dir, "Dropped.dll");
        File.Copy(Path.Combine(plugin.Dir, "hotctx.dll"), late);
        var context = (AssemblyLoadContext)Activator.CreateInstance(typeof(PluginManager).GetNestedType("PluginLoadContext", BindingFlags.NonPublic)!, Path.Combine(plugin.Dir, "hotctx.dll"), false, snapshot)!;

        var ex = Assert.ThrowsAny<Exception>(() => context.LoadFromAssemblyName(new AssemblyName("Dropped")));
        Assert.True(ex is FileLoadException || ex is FileNotFoundException, ex.GetType().Name);
        Assert.False(context.Assemblies.Any(a => a.GetName().Name == "Dropped"));
        context.Unload();
    }

    // ---- Review round: a copy under another folder, a failing start, native libraries, settings and events of a stopped plugin ----

    [Fact]
    public async Task The_same_plugin_id_in_another_folder_does_not_start_a_second_copy_after_an_unclean_stop()
    {
        var first = Build("hotid1", "hotidshared");
        var manager = Manager(Trusted(first));
        await manager.LoadNowAsync("hotid1");
        var stop = await manager.StopAsync("hotid1");
        Assert.Equal(PluginStop.NoDispose, stop.Stop);

        var copy = Build("hotid2", "hotidshared");
        Trusted(copy);
        var info = Assert.Single(await manager.LoadNowAsync("hotid2"));

        Assert.False(info.Loaded);
        Assert.True(info.NeedsRestartToStart);
        Assert.Null(info.Error);
        Assert.Empty(manager.Functions);
        Assert.Null(manager.GrantOf("hotidshared"));
    }

    [Fact]
    public async Task The_same_plugin_id_may_start_again_in_another_folder_after_a_clean_stop()
    {
        var first = Build("hotidc1", "hotidclean", Mode.DisposeMarks);
        var manager = Manager(Trusted(first));
        await manager.LoadNowAsync("hotidc1");
        Assert.True((await manager.StopAsync("hotidc1")).FullyStopped);

        var copy = Build("hotidc2", "hotidclean", Mode.DisposeMarks);
        Trusted(copy);

        Assert.True(Assert.Single(await manager.LoadNowAsync("hotidc2")).Loaded);
    }

    [Fact]
    public async Task A_failed_start_that_cannot_be_stopped_keeps_the_files_and_blocks_the_folder_and_the_id()
    {
        var plugin = Build("hotfailkeep", "hotfailkeep", Mode.InitThrowsAfterRegistering);
        var manager = Manager(Trusted(plugin));

        var info = Assert.Single(await manager.LoadNowAsync("hotfailkeep"));

        Assert.False(info.Loaded);
        Assert.Equal(PluginStop.NoDispose, manager.UnstoppedRun("hotfailkeep"));
        // Its code may still run: the files stay held.
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(Path.Combine(plugin.Dir, "hotfailkeep.dll"), [1]));
        var again = Assert.Single(await manager.LoadNowAsync("hotfailkeep"));
        Assert.True(again.NeedsRestartToStart);
    }

    [Fact]
    public async Task A_failed_start_with_a_clean_stop_lets_go_and_may_be_tried_again()
    {
        var plugin = Build("hotfailclean", "hotfailclean", Mode.InitThrowsAfterRegistering | Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));

        var info = Assert.Single(await manager.LoadNowAsync("hotfailclean"));

        Assert.False(info.Loaded);
        Assert.True(File.Exists(plugin.Marker));
        Assert.Null(manager.UnstoppedRun("hotfailclean"));
        Assert.Equal("kaputt", Assert.Single(await manager.LoadNowAsync("hotfailclean")).Error);
    }

    [Fact]
    public async Task A_stopped_plugin_reads_and_writes_no_settings_and_the_store_is_not_made_again()
    {
        var plugin = Build("hotsettings", "hotsettings", Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotsettings");
        var ctx = CtxOf(plugin)!;
        ctx.Settings.Set("k", "v");
        Assert.Equal("v", ctx.Settings.Get("k"));
        var file = Path.Combine(DataDir, "plugin-data", "hotsettings", "settings.json");
        Assert.True(File.Exists(file));

        await manager.StopAsync("hotsettings");
        var before = File.ReadAllText(file);
        ctx.Settings.Set("k", "changed");
        ctx.Settings.SetSecret("s", "x");

        Assert.Null(ctx.Settings.Get("k"));
        Assert.True(ctx.Settings.GetBool("flag", true));
        Assert.Equal(before, File.ReadAllText(file));
        // And a data folder that is gone is not made again for it.
        Directory.Delete(Path.Combine(DataDir, "plugin-data", "hotsettings"), recursive: true);
        _ = ctx.DataDirectory;
        _ = ctx.Settings.Get("k");
        Assert.False(Directory.Exists(Path.Combine(DataDir, "plugin-data", "hotsettings")));
    }

    [Fact]
    public async Task A_plugin_with_event_subscriptions_is_collected_after_the_stop_and_its_handlers_are_gone()
    {
        var plugin = Build("hotsub", "hotsub", Mode.DisposeMarks | Mode.SubscribesEvent);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotsub");
        var ctx = CtxOf(plugin)!;

        var result = await manager.StopAsync("hotsub");

        Assert.True(result.FullyStopped);
        Assert.True(result.Collected, "the load context stayed alive: a handler of the plugin is still held by the host");
        // No new subscription after the stop.
        var called = 0;
        ctx.Events.LanguageChanged += _ => Interlocked.Increment(ref called);
        manager.Events.RaiseLanguageChanged("en");
        await Task.Delay(150);
        Assert.Equal(0, called);
    }

    [Fact]
    public void A_native_library_that_was_not_hashed_is_refused_whatever_else_is_on_the_search_path()
    {
        var folder = Path.Combine(_root, "native");
        bool In(string path) => path.EndsWith("good.dll", StringComparison.OrdinalIgnoreCase);
        bool Exists(string path) => path.EndsWith("good.dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith("dropped.dll", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(NativeLookup.Snapshot, PluginNativeRules.Locate("good", folder, null, In, Exists).Result);
        Assert.Equal(NativeLookup.Snapshot, PluginNativeRules.Locate("good.dll", folder, null, In, Exists).Result);
        Assert.Equal(NativeLookup.Refused, PluginNativeRules.Locate("dropped", folder, null, In, Exists).Result);
        Assert.Equal(NativeLookup.Refused, PluginNativeRules.Locate("dropped.dll", folder, null, In, Exists).Result);
        // Not in the folder at all: only a system library by plain name may follow.
        Assert.Equal(NativeLookup.NotInFolder, PluginNativeRules.Locate("kernel32", folder, null, In, Exists).Result);
        // A name that leads out of the folder is not looked for in it.
        Assert.Equal(NativeLookup.NotInFolder, PluginNativeRules.Locate(@"..\evil", folder, null, In, Exists).Result);
        Assert.False(PluginNativeRules.IsPlainName(@"..\evil"));
        Assert.False(PluginNativeRules.IsPlainName(@"C:\x.dll"));
        Assert.False(PluginNativeRules.IsPlainName("a/b"));
        Assert.True(PluginNativeRules.IsPlainName("kernel32.dll"));
        // The resolver's own answer counts too: a listed one wins nothing over an unlisted one with the same name.
        Assert.Equal(NativeLookup.Refused, PluginNativeRules.Locate("good", folder, Path.Combine(folder, "dropped.dll"), In, Exists).Result);
    }

    [Fact]
    public void The_load_context_refuses_a_native_file_that_appeared_after_the_hash_and_a_resolver_covers_the_imports()
    {
        var plugin = Build("hotnative", "hotnative");
        using var snapshot = PluginFolderSnapshot.Open(plugin.Dir);
        var main = Path.Combine(plugin.Dir, "hotnative.dll");
        File.WriteAllBytes(Path.Combine(plugin.Dir, "dropped.dll"), [0x4d, 0x5a]);
        var type = typeof(PluginManager).GetNestedType("PluginLoadContext", BindingFlags.NonPublic)!;
        var context = (AssemblyLoadContext)Activator.CreateInstance(type, main, false, snapshot)!;

        var load = type.GetMethod("LoadUnmanagedDll", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var thrown = Assert.Throws<TargetInvocationException>(() => load.Invoke(context, ["dropped"]));
        Assert.IsType<DllNotFoundException>(thrown.InnerException);
        // Nothing of that name: left to the system search of the runtime (and the import resolver, which throws for anything not allowed).
        Assert.Equal(IntPtr.Zero, (IntPtr)load.Invoke(context, ["nothingatall"])!);
        var resolve = type.GetMethod("ResolveNative", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var refused = Assert.Throws<TargetInvocationException>(() => resolve.Invoke(context, ["dropped.dll", typeof(object).Assembly, null]));
        Assert.IsType<DllNotFoundException>(refused.InnerException);
        var elsewhere = Assert.Throws<TargetInvocationException>(() => resolve.Invoke(context, [@"C:\Windows\Temp\x.dll", typeof(object).Assembly, null]));
        Assert.IsType<DllNotFoundException>(elsewhere.InnerException);
        // The loader's own entry registers the resolver on the assembly it loads.
        Assert.NotNull(type.GetMethod("LoadChecked", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public));
        context.Unload();
    }

    [Fact]
    public void A_held_folder_cannot_be_renamed_and_text_from_a_plugin_is_one_clean_line()
    {
        var plugin = Build("hotfolders", "hotfolders");
        Directory.CreateDirectory(Path.Combine(plugin.Dir, "sub"));
        File.WriteAllText(Path.Combine(plugin.Dir, "sub", "a.txt"), "x");
        using (var snapshot = PluginFolderSnapshot.Open(plugin.Dir))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(plugin.Dir, "sub"), Path.Combine(plugin.Dir, "sub2")));
            Assert.ThrowsAny<IOException>(() => Directory.Delete(Path.Combine(plugin.Dir, "sub"), recursive: true));
        }
        Directory.Move(Path.Combine(plugin.Dir, "sub"), Path.Combine(plugin.Dir, "sub2"));

        var line = PluginSettingRules.LogLine("one\r\ntwo\u2028three\u0007" + new string('x', 500));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\u2028', line);
        Assert.True(line.Length <= 200);
        Assert.Equal("", PluginSettingRules.LogLine(null));
    }

    [Fact]
    public void A_listed_native_library_is_not_loaded_while_an_unchecked_file_lies_in_the_folder_tree()
    {
        var plugin = Build("hotdeps", "hotdeps");
        using var snapshot = PluginFolderSnapshot.Open(plugin.Dir);
        Directory.CreateDirectory(Path.Combine(plugin.Dir, "sub"));
        File.WriteAllBytes(Path.Combine(plugin.Dir, "sub", "dependency.dll"), [0x4d, 0x5a]);
        var type = typeof(PluginManager).GetNestedType("PluginLoadContext", BindingFlags.NonPublic)!;
        var context = (AssemblyLoadContext)Activator.CreateInstance(type, Path.Combine(plugin.Dir, "hotdeps.dll"), false, snapshot)!;
        var load = type.GetMethod("LoadUnmanagedDll", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // "hotdeps" is a listed file; the dropped dependency next to it makes the load fail closed (before anything is loaded).
        var thrown = Assert.Throws<TargetInvocationException>(() => load.Invoke(context, ["hotdeps"]));
        Assert.IsType<DllNotFoundException>(thrown.InnerException);
        Assert.False((bool)type.GetProperty("LoadedNative", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!);
        context.Unload();
    }

    [Fact]
    public async Task A_plugin_that_loaded_a_native_library_is_cut_off_but_cannot_start_again_before_the_restart()
    {
        var plugin = Build("hotnativeflag", "hotnativeflag", Mode.DisposeMarks);
        var manager = Manager(Trusted(plugin));
        await manager.LoadNowAsync("hotnativeflag");
        var running = (System.Collections.IDictionary)typeof(PluginManager).GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        var context = running["hotnativeflag"]!.GetType().GetProperty("Context")!.GetValue(running["hotnativeflag"])!;
        context.GetType().GetMethod("MarkNativeLoaded", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(context, null);

        var result = await manager.StopAsync("hotnativeflag");

        Assert.Equal(PluginStop.NativeModule, result.Stop);
        Assert.False(result.FullyStopped);
        Assert.Empty(manager.Functions);
        Assert.True(Assert.Single(manager.Plugins).StopsAfterRestart);
        // The files stay held and the folder does not start again in this process.
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(Path.Combine(plugin.Dir, "hotnativeflag.dll"), [1]));
        var again = Assert.Single(await manager.LoadNowAsync("hotnativeflag"));
        Assert.False(again.Loaded);
        Assert.True(again.NeedsRestartToStart);
    }

    [Fact]
    public async Task Subscribing_while_the_plugin_is_cut_off_never_leaves_a_handler_on_the_host()
    {
        for (var round = 0; round < 100; round++)
        {
            var host = new PluginEvents();
            var events = new PausableEvents(host, () => false);
            var called = 0;
            var gate = new ManualResetEventSlim();
            var subscribers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                gate.Wait();
                for (var i = 0; i < 25; i++) events.LanguageChanged += _ => Interlocked.Increment(ref called);
            })).ToArray();
            var closer = Task.Run(() =>
            {
                gate.Wait();
                events.Close();
            });
            gate.Set();
            await Task.WhenAll([.. subscribers, closer]);

            // Closed and every subscriber done: whatever was subscribed was taken back, nothing is left to be called.
            host.RaiseLanguageChanged("en");
            await Task.Delay(5);
            Assert.Equal(0, Volatile.Read(ref called));
        }
    }

    // ---- The rules ----

    [Theory]
    [InlineData(PluginHotAction.Start, null, PluginHotOutcome.Now)]
    [InlineData(PluginHotAction.Start, PluginStop.Clean, PluginHotOutcome.Now)]
    [InlineData(PluginHotAction.Start, PluginStop.NoDispose, PluginHotOutcome.AtRestart)]
    [InlineData(PluginHotAction.Start, PluginStop.TimedOut, PluginHotOutcome.AtRestart)]
    [InlineData(PluginHotAction.Start, PluginStop.Failed, PluginHotOutcome.AtRestart)]
    [InlineData(PluginHotAction.Update, null, PluginHotOutcome.AtRestart)]
    [InlineData(PluginHotAction.Update, PluginStop.Clean, PluginHotOutcome.AtRestart)]
    public void Starting_is_immediate_unless_an_earlier_run_could_not_be_stopped_and_an_update_always_waits(PluginHotAction action, PluginStop? earlier, PluginHotOutcome expected) =>
        Assert.Equal(expected, PluginLifecycleRules.Decide(action, earlier));

    [Theory]
    [InlineData(PluginHotAction.Disable, PluginStop.Clean, PluginHotOutcome.Now)]
    [InlineData(PluginHotAction.Disable, PluginStop.NoDispose, PluginHotOutcome.CutOffRestartForRest)]
    [InlineData(PluginHotAction.Uninstall, PluginStop.TimedOut, PluginHotOutcome.CutOffRestartForRest)]
    [InlineData(PluginHotAction.Uninstall, PluginStop.Failed, PluginHotOutcome.CutOffRestartForRest)]
    public void Stopping_is_complete_only_when_the_plugin_confirmed_it(PluginHotAction action, PluginStop stop, PluginHotOutcome expected) =>
        Assert.Equal(expected, PluginLifecycleRules.Decide(action, null, runningNow: true, thisStop: stop));

    [Fact]
    public void Stopping_something_that_did_not_run_is_immediate_and_the_worst_answer_wins()
    {
        Assert.Equal(PluginHotOutcome.Now, PluginLifecycleRules.Decide(PluginHotAction.Disable, null, runningNow: false));
        Assert.Equal(PluginStop.TimedOut, PluginLifecycleRules.Worst([PluginStop.Clean, PluginStop.NoDispose, PluginStop.TimedOut, PluginStop.Failed]));
        Assert.Equal(PluginStop.Failed, PluginLifecycleRules.Worst([PluginStop.Clean, PluginStop.Failed]));
        Assert.Equal(PluginStop.Clean, PluginLifecycleRules.Worst([]));
        Assert.Equal(PluginStop.NativeModule, PluginLifecycleRules.Worst([PluginStop.Clean, PluginStop.NativeModule]));
        Assert.False(PluginLifecycleRules.FullyStopped(PluginStop.NativeModule));
        Assert.Equal(PluginHotOutcome.CutOffRestartForRest, PluginLifecycleRules.Decide(PluginHotAction.Disable, null, runningNow: true, thisStop: PluginStop.NativeModule));
        Assert.Equal(PluginHotOutcome.AtRestart, PluginLifecycleRules.Decide(PluginHotAction.Start, PluginStop.NativeModule));
    }

    // ---- Hot unload leaves nothing of the plugin in the host's SDK 5 pieces ----

    private Built BuildFirstParty(string folder, string pluginId, params string[] capabilities)
    {
        var plugin = Build(folder, pluginId, Mode.DisposeMarks);
        File.WriteAllText(Path.Combine(plugin.Dir, "plugin.json"),
            "{\"id\":\"" + pluginId + "\",\"version\":\"1.0.0\",\"sdk\":5,\"capabilities\":[" + string.Join(",", capabilities.Select(c => "\"" + c + "\"")) + "]}");
        return plugin;
    }

    [Fact]
    public async Task Sensor_values_of_a_publisher_are_off_the_board_after_a_hot_unload()
    {
        var plugin = BuildFirstParty("hotsense", "hotsense", PluginCapabilities.SensorsPublish);
        var manager = Manager(Trusted(plugin, PluginOrigin.FirstParty, [PluginCapabilities.SensorsPublish], "hotsense"));
        manager.LoadAll();
        var ctx = CtxOf(plugin)!;
        ctx.PublishSensors([new PluginSensorReading(PluginSensorKind.Fan, "Pump", 2400)]);
        Assert.NotNull(manager.SensorBoard.Read(_ => true));

        var result = await manager.StopAsync("hotsense");

        Assert.True(result.WasRunning);
        Assert.Null(manager.SensorBoard.Read(_ => true));
        // A thread of the stopped plugin that publishes later reaches nothing (its context is dead).
        ctx.PublishSensors([new PluginSensorReading(PluginSensorKind.Fan, "Pump", 2500)]);
        Assert.Null(manager.SensorBoard.Read(_ => true));
    }

    [Fact]
    public async Task A_TV_provider_is_out_of_the_registry_after_a_hot_unload()
    {
        var plugin = BuildFirstParty("hottv", "hottv", PluginCapabilities.TvControl);
        var manager = Manager(Trusted(plugin, PluginOrigin.FirstParty, [PluginCapabilities.TvControl], "hottv"));
        // The registry normally answers only for running plugins; open here, so that only its own list is looked at.
        manager.TvProviders.IsRunning = _ => true;
        manager.LoadAll();
        var ctx = CtxOf(plugin)!;
        ctx.RegisterTvProvider(new TestTvProvider(3001, 3000) { Id = "hot-tv", Name = "Hot TV" });
        Assert.Equal("hottv", manager.TvProviders.PluginOf("hot-tv"));

        await manager.StopAsync("hottv");

        Assert.Null(manager.TvProviders.PluginOf("hot-tv"));
        Assert.Empty(manager.TvProviders.Available());
    }

    // ---- What the install answer says after the start (Home Assistant "restart needed" right after a fresh install) ----

    private static PluginInfo Line(bool enabled = true, bool loaded = false, string? error = null) => new("f", "f", "F", "1.0.0", null, null, null, enabled, loaded, error, true);

    [Fact]
    public async Task A_fresh_install_of_a_folder_the_owner_switched_off_earlier_says_switched_off_not_restart()
    {
        // The owner's "off" outlives an uninstall (a reinstall must not lift it): the plugin is installed but does not start, and no restart
        // would change that. The answer must say so instead of "loads on the next start".
        var plugin = Build("hotoff", "hotoff");
        var manager = Manager(Trusted(plugin, PluginOrigin.FirstParty), enabled: _ => false);

        var infos = await manager.LoadNowAsync("hotoff");

        Assert.False(Assert.Single(infos).Loaded);
        Assert.Equal(InstalledStart.SwitchedOff, InstalledStartRules.Judge(infos));
        Assert.False(manager.IsRunning("hotoff"));
        Assert.Contains("ausgeschaltet", Strings.GetIn("de", "catalog.pluginInstalledOff", "Home Assistant", "1.2.0"));
        Assert.Contains("switched off", Strings.GetIn("en", "catalog.pluginInstalledOff", "Home Assistant", "1.2.0"));
    }

    [Fact]
    public async Task A_fresh_install_that_is_switched_on_starts_hot_and_needs_no_restart()
    {
        var plugin = Build("hoton", "hoton");
        var manager = Manager(Trusted(plugin, PluginOrigin.FirstParty));

        var infos = await manager.LoadNowAsync("hoton");

        Assert.Equal(InstalledStart.Started, InstalledStartRules.Judge(infos));
        Assert.True(manager.IsRunning("hoton"));
    }

    [Fact]
    public void The_answer_after_a_start_only_asks_for_a_restart_when_one_really_is_owed()
    {
        Assert.Equal(InstalledStart.Started, InstalledStartRules.Judge([Line(loaded: true)]));
        Assert.Equal(InstalledStart.SwitchedOff, InstalledStartRules.Judge([Line(enabled: false)]));
        Assert.Equal(InstalledStart.Failed, InstalledStartRules.Judge([Line(error: "kaputt")]));
        Assert.Equal(InstalledStart.AtRestart, InstalledStartRules.Judge([Line() with { NeedsRestartToStart = true }]));
        Assert.Equal(InstalledStart.AtRestart, InstalledStartRules.Judge([Line(enabled: false) with { NeedsRestartToStart = true }]));
        Assert.Equal(InstalledStart.AtRestart, InstalledStartRules.Judge([Line() with { SkippedForOffline = true }]));
        Assert.Equal(InstalledStart.AtRestart, InstalledStartRules.Judge([Line(error: "unknown") with { NeedsApproval = true }]));
        Assert.Equal(InstalledStart.AtRestart, InstalledStartRules.Judge([]));
    }

    // ---- Removal after the stop (retries) ----

    [Fact]
    public async Task After_a_clean_stop_the_folder_is_removed_at_once_without_a_pause()
    {
        var plugin = Build("hotclean", "hotclean", Mode.DisposeMarks);
        var trust = Trusted(plugin);
        var manager = Manager(trust);
        await manager.LoadNowAsync("hotclean");
        trust.MarkRemoved("hotclean");
        var stop = await manager.StopAsync("hotclean");
        Assert.True(stop.FullyStopped);
        var pauses = 0;

        var gone = await CatalogService.RemoveAfterStopAsync(DataDir, "hotclean", 3, TimeSpan.Zero, _ => { pauses++; return Task.CompletedTask; });

        Assert.True(gone);
        Assert.Equal(0, pauses);
        Assert.False(Directory.Exists(plugin.Dir));
        Assert.Empty(trust.RemovedFolders());
    }

    [Fact]
    public async Task A_folder_that_is_held_for_a_moment_is_removed_by_a_later_try()
    {
        var plugin = Build("hotlate", "hotlate");
        var trust = Trusted(plugin);
        trust.MarkRemoved("hotlate");
        var held = new FileStream(Path.Combine(plugin.Dir, "held.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var pauses = 0;

        var gone = await CatalogService.RemoveAfterStopAsync(DataDir, "hotlate", 3, TimeSpan.Zero, _ =>
        {
            // The runtime lets go during the second pause.
            if (++pauses == 2) held.Dispose();
            return Task.CompletedTask;
        });
        held.Dispose();

        Assert.True(gone);
        Assert.Equal(2, pauses);
        Assert.False(Directory.Exists(plugin.Dir));
        Assert.Empty(trust.RemovedFolders());
    }

    [Fact]
    public async Task A_folder_that_stays_held_is_tried_a_few_times_then_left_marked_for_the_next_start()
    {
        var plugin = Build("hotheld", "hotheld");
        var trust = Trusted(plugin);
        trust.MarkRemoved("hotheld");
        using var held = new FileStream(Path.Combine(plugin.Dir, "held.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var pauses = 0;

        var gone = await CatalogService.RemoveAfterStopAsync(DataDir, "hotheld", 3, TimeSpan.Zero, _ => { pauses++; return Task.CompletedTask; });

        Assert.False(gone);
        Assert.Equal(3, pauses);
        Assert.True(Directory.Exists(plugin.Dir));
        Assert.Contains("hotheld", trust.RemovedFolders());
    }
}
