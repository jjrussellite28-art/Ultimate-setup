using CouchHud.Core.Guard;

namespace CouchHud.Core.Tests;

public sealed class LockHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-hard-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly List<string> _log = [];

    public LockHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private GuardSnapshot Snapshot() => new(_reg, new FileVdfStore(), Path.Combine(_dir, "guard-locked.json"), _log.Add);

    [Fact]
    public void LocksTheShellToolsAndPutsEverythingBack()
    {
        var snapshot = Snapshot();
        Assert.Empty(snapshot.Apply(LockHardening.Changes(_reg)));

        foreach (var name in new[] { "NoControlPanel", "NoRun", "NoWinKeys", "NoViewContextMenu", "NoFolderOptions", "DisallowRun" })
            Assert.Equal(1, _reg.ReadDword(LockHardening.ExplorerKey, name));
        Assert.Equal(1, _reg.ReadDword(LockHardening.SystemKey, "DisableChangePassword"));
        Assert.Equal(0, _reg.ReadDword(LockHardening.NarratorKey, "WinEnterLaunchEnabled"));
        Assert.Equal(0, _reg.ReadDword(GameBarGuard.CaptureKey, "AppCaptureEnabled"));
        Assert.Equal(LockHardening.BlockedPrograms.OrderBy(x => x), _reg.ReadValues(LockHardening.DisallowRunKey).Values.OrderBy(x => x));

        Assert.Empty(snapshot.Restore());
        Assert.False(snapshot.HasPending);
        Assert.Null(_reg.ReadDword(LockHardening.ExplorerKey, "NoRun"));
        Assert.Null(_reg.ReadDword(GameBarGuard.CaptureKey, "AppCaptureEnabled"));
        Assert.Empty(_reg.ReadValues(LockHardening.DisallowRunKey));
    }

    [Fact]
    public void TheUsersOwnValuesComeBackExactly()
    {
        _reg.WriteDword(LockHardening.ExplorerKey, "NoRun", 0);
        _reg.WriteDword(LockHardening.ExplorerKey, "NoControlPanel", 1);
        _reg.WriteDword(GameBarGuard.CaptureKey, "AppCaptureEnabled", 1);
        _reg.WriteString(LockHardening.DisallowRunKey, "1", "game-cheat.exe");
        _reg.WriteString(LockHardening.DisallowRunKey, "2", "other.exe");
        var snapshot = Snapshot();

        snapshot.Apply(LockHardening.Changes(_reg));
        Assert.Equal(1, _reg.ReadDword(LockHardening.ExplorerKey, "NoRun"));
        Assert.Equal(0, _reg.ReadDword(GameBarGuard.CaptureKey, "AppCaptureEnabled"));
        // The user's list stays, ours are added under free numbers.
        Assert.Equal("game-cheat.exe", _reg.ReadString(LockHardening.DisallowRunKey, "1"));
        Assert.Equal("other.exe", _reg.ReadString(LockHardening.DisallowRunKey, "2"));
        Assert.Equal(2 + LockHardening.BlockedPrograms.Count, _reg.ReadValues(LockHardening.DisallowRunKey).Count);

        snapshot.Restore();
        Assert.Equal(0, _reg.ReadDword(LockHardening.ExplorerKey, "NoRun"));
        Assert.Equal(1, _reg.ReadDword(LockHardening.ExplorerKey, "NoControlPanel"));
        Assert.Equal(1, _reg.ReadDword(GameBarGuard.CaptureKey, "AppCaptureEnabled"));
        var left = _reg.ReadValues(LockHardening.DisallowRunKey);
        Assert.Equal(2, left.Count);
        Assert.Equal("game-cheat.exe", left["1"]);
        Assert.Equal("other.exe", left["2"]);
    }

    [Fact]
    public void AProgramThatIsListedAlreadyIsNotAddedTwice()
    {
        _reg.WriteString(LockHardening.DisallowRunKey, "1", "CMD.EXE");
        var changes = LockHardening.Changes(_reg);
        Assert.DoesNotContain(changes, c => c.Name != "DisallowRun" && c.Location == LockHardening.DisallowRunKey && c.Value == "cmd.exe");
        Assert.Equal(LockHardening.BlockedPrograms.Count - 1, changes.Count(c => c.Location == LockHardening.DisallowRunKey));
    }

    [Fact]
    public void ACrashLeavesNothingBehind()
    {
        _reg.WriteDword(LockHardening.ExplorerKey, "NoRun", 0);
        Snapshot().Apply(LockHardening.Changes(_reg));
        Assert.Equal(1, _reg.ReadDword(LockHardening.ExplorerKey, "NoRun"));

        // The next start (a new process) restores from the saved state.
        var next = Snapshot();
        Assert.True(next.HasPending);
        Assert.Empty(next.RestoreLeftovers());
        Assert.Equal(0, _reg.ReadDword(LockHardening.ExplorerKey, "NoRun"));
        Assert.Null(_reg.ReadDword(LockHardening.SystemKey, "DisableChangePassword"));
        Assert.Empty(_reg.ReadValues(LockHardening.DisallowRunKey));
    }

    [Fact]
    public void ApplyingTwiceKeepsTheFirstBackup()
    {
        _reg.WriteDword(LockHardening.ExplorerKey, "NoRun", 0);
        var snapshot = Snapshot();
        snapshot.Apply(LockHardening.Changes(_reg));
        snapshot.Apply(LockHardening.Changes(_reg));
        snapshot.Restore();
        Assert.Equal(0, _reg.ReadDword(LockHardening.ExplorerKey, "NoRun"));
        Assert.Empty(_reg.ReadValues(LockHardening.DisallowRunKey));
    }

    [Fact]
    public void AValueOfTheWrongTypeIsLeftAloneAndTheRestStillApplies()
    {
        _reg.WriteString(LockHardening.ExplorerKey, "NoRun", "yes");
        var errors = Snapshot().Apply(LockHardening.Changes(_reg));
        Assert.Single(errors);
        Assert.Equal("yes", _reg.ReadString(LockHardening.ExplorerKey, "NoRun"));
        Assert.Equal(1, _reg.ReadDword(LockHardening.ExplorerKey, "NoWinKeys"));
    }

    [Fact]
    public void RegistryToolsStayUsableForSilentImports() =>
        Assert.DoesNotContain(LockHardening.Changes(_reg), c => c.Name == "DisableRegistryTools");

    /// <summary>A registry whose writes fail once the state is saved: the process dies between Save and Write.</summary>
    private sealed class DyingRegistry(FakeRegistry inner) : CouchHud.Core.Platform.IRegistry
    {
        public bool Dead { get; set; }
        public string? ReadString(string key, string name) => inner.ReadString(key, name);
        public IReadOnlyDictionary<string, string> ReadValues(string key) => inner.ReadValues(key);
        public byte[]? ReadBinary(string key, string name) => inner.ReadBinary(key, name);
        public int? ReadDword(string key, string name) => inner.ReadDword(key, name);
        public CouchHud.Core.Platform.RegistryValueType GetValueType(string key, string name) => inner.GetValueType(key, name);
        public void WriteString(string key, string name, string value) { if (Dead) throw new IOException("died"); inner.WriteString(key, name, value); }
        public void WriteDword(string key, string name, int value) { if (Dead) throw new IOException("died"); inner.WriteDword(key, name, value); }
        public void DeleteValue(string key, string name) => inner.DeleteValue(key, name);
    }

    [Fact]
    public void ACrashBetweenSaveAndWriteStillRestoresTheOriginals()
    {
        _reg.WriteDword(LockHardening.ExplorerKey, "NoRun", 0);
        var dying = new DyingRegistry(_reg) { Dead = true };
        var errors = new GuardSnapshot(dying, new FileVdfStore(), Path.Combine(_dir, "guard-locked.json"), _log.Add).Apply(LockHardening.Changes(_reg));
        Assert.NotEmpty(errors);

        // The next run restores from the state that was saved before the first write: the user's 0 is still 0, nothing is left over.
        Assert.Empty(Snapshot().RestoreLeftovers());
        Assert.Equal(0, _reg.ReadDword(LockHardening.ExplorerKey, "NoRun"));
        Assert.Null(_reg.ReadDword(LockHardening.ExplorerKey, "NoWinKeys"));
    }

    [Fact]
    public void KeysWeCreatedGoAgainWhenEmptyButTheUsersKeysStay()
    {
        // The user has the Explorer policy key (with a value of their own), not the others.
        _reg.WriteDword(LockHardening.ExplorerKey, "NoDrives", 4);
        var created = LockHardening.MissingKeys(_reg);
        Assert.DoesNotContain(LockHardening.ExplorerKey, created);
        Assert.Contains(LockHardening.DisallowRunKey, created);
        Assert.Contains(LockHardening.WindowsSystemKey, created);

        var snapshot = Snapshot();
        snapshot.Apply(LockHardening.Changes(_reg));
        snapshot.Restore();
        LockHardening.RemoveEmpty(_reg, created);

        Assert.False(_reg.KeyExists(LockHardening.DisallowRunKey));
        Assert.False(_reg.KeyExists(LockHardening.WindowsSystemKey));
        Assert.False(_reg.KeyExists(@"HKCU\Software\Policies\Microsoft\Windows"));
        Assert.True(_reg.KeyExists(LockHardening.ExplorerKey));
        Assert.Equal(4, _reg.ReadDword(LockHardening.ExplorerKey, "NoDrives"));
    }

    [Fact]
    public void AKeyThatStillHasValuesIsNotRemoved()
    {
        var created = LockHardening.MissingKeys(_reg);
        _reg.WriteDword(LockHardening.WindowsSystemKey, "SomethingElse", 1);
        LockHardening.RemoveEmpty(_reg, created);
        Assert.Equal(1, _reg.ReadDword(LockHardening.WindowsSystemKey, "SomethingElse"));
    }

    [Fact]
    public void ScriptsStayPossibleBecauseOnlyTheInteractiveCommandPromptIsDisabled()
    {
        // 1 would stop every "cmd /c" of a launcher, a startup entry or a game; 2 only the interactive prompt.
        Assert.Equal("2", LockHardening.Changes(_reg).Single(c => c.Name == "DisableCMD").Value);
    }

    [Fact]
    public void AccessibilityShortcutsLoseOnlyTheHotkeyFlag()
    {
        // Sticky Keys default flags 510: feature off, hotkey (4) on.
        var off = LockHardening.WithoutHotkey(510);
        Assert.Equal(510u & ~4u, off);
        Assert.Equal(0u, off & LockHardening.HotkeyActive);
        // Unlock: the user's own bit comes back, flags the user changed meanwhile stay.
        Assert.Equal(510u, LockHardening.WithHotkeyRestored(off, 510));
        Assert.Equal(off, LockHardening.WithHotkeyRestored(off, off));
        Assert.Equal((511u & ~4u), LockHardening.WithHotkeyRestored(511, off));
        Assert.Equal(510u | 1u, LockHardening.WithHotkeyRestored(off | 1u, 510));
    }
}
