using System.Text;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

/// <summary>Steam's own settings for the couch: apply, put back, Steam writing in between, missing files, several accounts.</summary>
public sealed class SteamGuardTests : IDisposable
{
    private const string Steam = @"C:\Steam";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-steamguard-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly FakeVdf _vdf = new();
    private readonly List<string> _log = [];

    public SteamGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string State => Path.Combine(_dir, SteamGuard.StateFile);

    private GuardSnapshot Create(IVdfStore? vdf = null) => new(_reg, vdf ?? _vdf, State, _log.Add);

    private static string Local(string id) => SteamGuard.LocalConfigPath(Steam, id);

    private const string Sample = "\"UserLocalConfigStore\"\r\n{\r\n\t\"system\"\r\n\t{\r\n\t\t\"OverlayLogCleanupDone\"\t\t\"1\"\r\n\t}\r\n\t// a comment\r\n\t\"Broadcast\"\r\n\t{\r\n\t\t\"Permissions\"\t\t\"1\"\r\n\t}\r\n}\r\n";

    private IReadOnlyList<GuardChange> Plan(params string[] ids) => SteamGuard.Changes(Steam, ids, _vdf, _reg);

    [Fact]
    public void ApplyWritesTheQuietValuesAndRestoreRemovesWhatWasMissing()
    {
        _vdf.Files[Local("100")] = Sample;
        var guard = Create();

        Assert.Empty(guard.Apply(Plan("100")));
        var text = _vdf.Files[Local("100")];
        Assert.Equal("0", VdfPatch.Get(text, "UserLocalConfigStore/Controller_CheckGuideButton"));
        Assert.Equal("0", VdfPatch.Get(text, "UserLocalConfigStore/system/EnableSCTenFootOverlayCheckNew"));
        Assert.Equal("0", VdfPatch.Get(text, "UserLocalConfigStore/system/ControllerConnectNotificationToast"));

        Assert.Empty(guard.Restore());
        Assert.Equal(Sample, _vdf.Files[Local("100")]);
        Assert.False(guard.HasPending);
    }

    [Fact]
    public void RestoreBringsBackTheUsersOwnValueExactly()
    {
        var original = Sample.Replace("\t// a comment", "\t\"Controller_CheckGuideButton\"\t\t\"1\"\r\n\t// a comment");
        _vdf.Files[Local("100")] = original;
        var guard = Create();

        guard.Apply(Plan("100"));
        Assert.Equal("0", VdfPatch.Get(_vdf.Files[Local("100")], "UserLocalConfigStore/Controller_CheckGuideButton"));

        guard.Restore();
        Assert.Equal(original, _vdf.Files[Local("100")]);
    }

    [Fact]
    public void SteamWritingInBetweenIsLeftAlone()
    {
        _vdf.Files[Local("100")] = Sample;
        var guard = Create();
        guard.Apply(Plan("100"));

        // Steam started, the user switched the Guide button option back on and Steam wrote its file again.
        _vdf.Files[Local("100")] = VdfPatch.Set(_vdf.Files[Local("100")], "UserLocalConfigStore/Controller_CheckGuideButton", "1");
        var edited = _vdf.Files[Local("100")];

        Assert.Empty(guard.Restore());
        var text = _vdf.Files[Local("100")];
        Assert.Equal("1", VdfPatch.Get(text, "UserLocalConfigStore/Controller_CheckGuideButton")); // theirs, not ours to put back
        // The two still ours go away again.
        Assert.Null(VdfPatch.Get(text, "UserLocalConfigStore/system/EnableSCTenFootOverlayCheckNew"));
        Assert.Null(VdfPatch.Get(text, "UserLocalConfigStore/system/ControllerConnectNotificationToast"));
        Assert.NotEqual(edited, text);
        Assert.False(guard.HasPending);
    }

    [Fact]
    public void ASteamThatRemovedTheKeyOrTheFileLeavesNothingToRestore()
    {
        _vdf.Files[Local("100")] = Sample;
        _vdf.Files[Local("200")] = Sample;
        var guard = Create();
        guard.Apply(Plan("100", "200"));

        _vdf.Files[Local("100")] = Sample; // rewritten without our keys
        _vdf.Files.Remove(Local("200"));   // gone altogether

        Assert.Empty(guard.Restore());
        Assert.Equal(Sample, _vdf.Files[Local("100")]);
        Assert.False(_vdf.Files.ContainsKey(Local("200")));
        Assert.False(guard.HasPending);
    }

    [Fact]
    public void WouldRestoreIsFalseOnceNothingIsOurs()
    {
        _vdf.Files[Local("100")] = Sample;
        var guard = Create();
        guard.Apply(Plan("100"));
        Assert.True(SteamGuard.WouldRestore(guard.Entries, _vdf, _reg));

        _vdf.Files[Local("100")] = Sample; // Steam wrote its own state
        Assert.False(SteamGuard.WouldRestore(guard.Entries, _vdf, _reg));
    }

    [Fact]
    public void AMissingFileIsNotCreated()
    {
        var changes = Plan("100");
        Assert.Empty(changes);
        Assert.False(_vdf.Files.ContainsKey(Local("100")));
    }

    [Fact]
    public void AFileThatCannotBeReadIsSkipped()
    {
        _vdf.Files[Local("100")] = "\"UserLocalConfigStore\" {";
        _vdf.Files[Local("200")] = Sample;
        var changes = Plan("100", "200");
        Assert.All(changes.Where(c => c.Kind == GuardKind.VdfKey), c => Assert.Equal(Local("200"), c.Location));
        Assert.Equal(SteamGuard.LocalConfigKeys.Count, changes.Count(c => c.Kind == GuardKind.VdfKey));
    }

    [Fact]
    public void EveryAccountOfTheMachineIsDone()
    {
        _vdf.Files[Local("100")] = Sample;
        _vdf.Files[Local("200")] = Sample;
        var guard = Create();

        Assert.Empty(guard.Apply(Plan("100", "200")));
        foreach (var id in new[] { "100", "200" })
            Assert.Equal("0", VdfPatch.Get(_vdf.Files[Local(id)], "UserLocalConfigStore/Controller_CheckGuideButton"));

        guard.Restore();
        Assert.Equal(Sample, _vdf.Files[Local("100")]);
        Assert.Equal(Sample, _vdf.Files[Local("200")]);
    }

    [Fact]
    public void OnlyNumericAccountFoldersCount()
    {
        Assert.Equal(["100", "20"], SteamGuard.UserIds(["100", "0", "ac", "20", "00", "..", "1 2", "100"]).OrderByDescending(x => x.Length));
    }

    [Fact]
    public void BigPictureAtStartIsTurnedOffOnlyWhereItWasOn()
    {
        _reg.WriteDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue, 4);
        var guard = Create();
        Assert.Empty(guard.Apply(Plan()));
        Assert.Equal(7, _reg.ReadDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue));

        guard.Restore();
        Assert.Equal(4, _reg.ReadDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue));
    }

    [Fact]
    public void AStartupModeTheUserNeverSetOrChangedIsNotTouched()
    {
        Assert.Empty(Plan());
        _reg.WriteDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue, 7);
        Assert.Empty(Plan());
        _reg.WriteDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue, 2);
        Assert.Empty(Plan());
    }

    [Fact]
    public void StartupModeChangedByTheUserSinceStaysOnRestore()
    {
        _reg.WriteDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue, 4);
        var guard = Create();
        guard.Apply(Plan());
        _reg.WriteDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue, 2);

        guard.Restore();
        Assert.Equal(2, _reg.ReadDword(SteamGuard.RegistryKey, SteamGuard.StartupModeValue));
    }

    [Fact]
    public void ACrashLeavesTheOriginalInTheStateFileForTheNextStart()
    {
        _vdf.Files[Local("100")] = Sample;
        Create().Apply(Plan("100"));

        var next = Create(); // a new process reads the state file
        Assert.True(next.HasPending);
        Assert.Empty(next.RestoreLeftovers());
        Assert.Equal(Sample, _vdf.Files[Local("100")]);
    }

    [Fact]
    public void ApplyingTwiceKeepsTheFirstOriginal()
    {
        _vdf.Files[Local("100")] = Sample;
        var guard = Create();
        guard.Apply(Plan("100"));
        guard.Apply(Plan("100"));
        guard.Restore();
        Assert.Equal(Sample, _vdf.Files[Local("100")]);
    }

    [Fact]
    public void WouldChangeIsFalseWhenSteamAlreadyReadsLikeThat()
    {
        var quiet = Sample.Replace("\t// a comment", "\t\"Controller_CheckGuideButton\"\t\t\"0\"\r\n\t\"EnableSCTenFootOverlayCheckNew\"\t\t\"0\"\r\n\t// a comment");
        quiet = VdfPatch.Set(quiet, "UserLocalConfigStore/system/EnableSCTenFootOverlayCheckNew", "0");
        quiet = VdfPatch.Set(quiet, "UserLocalConfigStore/system/ControllerConnectNotificationToast", "0");
        _vdf.Files[Local("100")] = quiet;
        Assert.False(SteamGuard.WouldChange(Plan("100"), _vdf, _reg));

        _vdf.Files[Local("100")] = Sample;
        Assert.True(SteamGuard.WouldChange(Plan("100"), _vdf, _reg));
    }

    [Theory]
    [InlineData(false, false, false, false, SteamGuardStep.None)]
    [InlineData(true, false, false, true, SteamGuardStep.PatchNow)]
    [InlineData(true, false, true, true, SteamGuardStep.PatchNow)]   // Steam is off: a running game is not Steam's
    [InlineData(true, true, false, true, SteamGuardStep.CycleSteam)]
    [InlineData(true, true, true, true, SteamGuardStep.Defer)]       // never during a game
    [InlineData(true, true, false, false, SteamGuardStep.Defer)]     // no cycle allowed (session end)
    public void PlanFollowsWhatSteamIsDoing(bool needs, bool steamRunning, bool game, bool allowCycle, SteamGuardStep expected) =>
        Assert.Equal(expected, SteamGuard.Plan(needs, steamRunning, game, allowCycle));

    [Fact]
    public void OnlyFullIntegrationOnTheCouchWithTheOptionOn()
    {
        Assert.True(SteamGuard.ShouldApply(full: true, pcMode: false, suppressCompetitors: true));
        Assert.False(SteamGuard.ShouldApply(full: false, pcMode: false, suppressCompetitors: true)); // light mode
        Assert.False(SteamGuard.ShouldApply(full: true, pcMode: true, suppressCompetitors: true));
        Assert.False(SteamGuard.ShouldApply(full: true, pcMode: false, suppressCompetitors: false));
    }

    [Fact]
    public void TheFileSystemStoreKeepsEveryOtherByteAndTheBom()
    {
        var file = Path.Combine(_dir, "localconfig.vdf");
        var original = "// header\r\n" + Sample.Replace("\t\"Broadcast\"", "\t\"Notes\"\t\t\"ü \\\"quoted\\\" \\\\path\"\r\n\t\"Broadcast\"");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(original)).ToArray();
        File.WriteAllBytes(file, bytes);
        var store = new FileVdfStore();
        var guard = new GuardSnapshot(_reg, store, State, _log.Add);
        var changes = SteamGuard.LocalConfigKeys.Select(k => new GuardChange(GuardKind.VdfKey, file, k.Key, k.Value, true)).ToList();

        Assert.Empty(guard.Apply(changes));
        var patched = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, patched.Take(3).ToArray());
        Assert.Contains("// a comment", Encoding.UTF8.GetString(patched));
        Assert.Contains("ü \\\"quoted\\\" \\\\path", Encoding.UTF8.GetString(patched));

        Assert.Empty(guard.Restore());
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void TheFileSystemStoreRestoresOnlyWhatIsStillOurs()
    {
        var file = Path.Combine(_dir, "localconfig.vdf");
        File.WriteAllText(file, Sample);
        var store = new FileVdfStore();
        var guard = new GuardSnapshot(_reg, store, State, _log.Add);
        guard.Apply(SteamGuard.LocalConfigKeys.Select(k => new GuardChange(GuardKind.VdfKey, file, k.Key, k.Value, true)));

        File.WriteAllText(file, VdfPatch.Set(File.ReadAllText(file), "UserLocalConfigStore/system/ControllerConnectNotificationToast", "1"));
        guard.Restore();

        var text = File.ReadAllText(file);
        Assert.Equal("1", VdfPatch.Get(text, "UserLocalConfigStore/system/ControllerConnectNotificationToast"));
        Assert.Null(VdfPatch.Get(text, "UserLocalConfigStore/Controller_CheckGuideButton"));
    }

    [Fact]
    public void StateWithoutTheNewFlagStillRestoresLikeBefore()
    {
        // A state file of an older version has no OnlyIfUnchanged: it behaves as before (always back).
        _reg.WriteDword("HKCU\\Software\\Test\\Old", "A", 1);
        File.WriteAllText(State, "[{\"Kind\":\"RegistryDword\",\"Location\":\"HKCU\\\\Software\\\\Test\\\\Old\",\"Name\":\"A\",\"Original\":\"5\",\"Applied\":\"0\"}]");
        var guard = Create();
        Assert.Empty(guard.Restore());
        Assert.Equal(5, _reg.ReadDword("HKCU\\Software\\Test\\Old", "A"));
    }

    [Fact]
    public void AnElevatedRestoreIgnoresTheSteamEntries()
    {
        // VDF files and Steam's registry key are not on the list of an administrator process.
        var vdfEntry = new GuardEntry(GuardKind.VdfKey, Local("100"), "UserLocalConfigStore/Controller_CheckGuideButton", null, "0", OnlyIfUnchanged: true);
        var regEntry = new GuardEntry(GuardKind.RegistryDword, SteamGuard.RegistryKey, SteamGuard.StartupModeValue, "4", "7", OnlyIfUnchanged: true);
        Assert.False(ElevatedGuard.Accepts(vdfEntry));
        Assert.False(ElevatedGuard.Accepts(regEntry));
    }

    /// <summary>VDF files in memory, with the same editing code as the real thing.</summary>
    private sealed class FakeVdf : IVdfStore
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Exists(string file) => Files.ContainsKey(file);

        public VdfLookup Lookup(string file, string keyPath) =>
            Files.TryGetValue(file, out var text) ? VdfPatch.Lookup(text, keyPath) : new VdfLookup(VdfLookupKind.Missing, null, null, 0);

        public void Write(string file, string keyPath, string value) =>
            Files[file] = VdfPatch.Set(Files.GetValueOrDefault(file, ""), keyPath, value);

        public void WriteRaw(string file, string keyPath, string rawToken) =>
            Files[file] = VdfPatch.SetRaw(Files.GetValueOrDefault(file, ""), keyPath, rawToken);

        public void Delete(string file, string keyPath, int? pruneFrom = null)
        {
            if (Files.TryGetValue(file, out var text)) Files[file] = VdfPatch.Delete(text, keyPath, pruneFrom);
        }

        public void DeleteFileIfBlank(string file)
        {
            if (Files.TryGetValue(file, out var text) && string.IsNullOrWhiteSpace(text)) Files.Remove(file);
        }
    }
}
