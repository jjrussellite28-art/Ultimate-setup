using System.IO.Compression;
using CouchHud.Core.Config;
using CouchHud.Core.Saves;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Data-loss findings of the review of the save game feature.</summary>
public sealed class SaveGameReviewTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "couchhud-saves2-" + Guid.NewGuid().ToString("N"));
    private static readonly GameRef Game = new("Real Game", "steam:4242");

    public SaveGameReviewTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_tmp, true);
        }
        catch (IOException) { }
    }

    private SaveContext Ctx(string? installDir = null) => new()
    {
        Home = Path.Combine(_tmp, "home"),
        WinAppData = Path.Combine(_tmp, "home", "AppData", "Roaming"),
        WinLocalAppData = Path.Combine(_tmp, "home", "AppData", "Local"),
        WinLocalAppDataLow = Path.Combine(_tmp, "home", "AppData", "LocalLow"),
        WinDocuments = Path.Combine(_tmp, "home", "Documents"),
        WinPublic = Path.Combine(_tmp, "pub"),
        WinProgramData = Path.Combine(_tmp, "pd"),
        WinDir = Path.Combine(_tmp, "win"),
        UserName = "tester",
        InstallDir = installDir,
        Store = "steam",
        SteamUserIds = ["111"]
    };

    private ManifestEntry SaveEntry(params string[] paths) =>
        new("Real Game") { Files = paths.Select(p => new ManifestPath(p, ["save"], [])).ToList() };

    private ManifestEntry Entry() => SaveEntry("<winAppData>/RealGame/saves/**/*.sav");

    private string Saves(SaveContext c) => Path.Combine(c.WinAppData, "RealGame", "saves");

    private void Write(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string GameDir => Path.Combine(_tmp, "backups", "Real Game [steam_4242]");

    private static Func<DateTime> Clock(DateTime start)
    {
        var t = start;
        return () => t = t.AddMinutes(1);
    }

    // --- H1: safety backups rotate apart ---

    [Fact]
    public void SafetyBackupsNeverEvictRegularOnesAndKeepTheirOwnLimit()
    {
        var ctx = Ctx();
        var entry = Entry();
        Write(Path.Combine(Saves(ctx), "a.sav"), "1");
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 2) { Now = Clock(new DateTime(2026, 1, 1, 10, 0, 0)) };
        var r1 = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;
        var r2 = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;

        for (var i = 0; i < 6; i++)
        {
            File.WriteAllText(Path.Combine(Saves(ctx), "a.sav"), "changed" + i);
            Assert.NotNull(svc.Restore(Game, r2, entry, ctx).SafetyBackupId);
        }

        var all = svc.List(Game);
        Assert.Equal(2, all.Count(b => !b.Safety));
        Assert.Contains(all, b => b.Id == r1);
        Assert.Contains(all, b => b.Id == r2);
        Assert.Equal(SaveBackupService.SafetyKeep, all.Count(b => b.Safety));
    }

    // --- H2: incomplete backups ---

    [Fact]
    public void BackupWithUnreadableFileIsMarkedIncompleteAndNotRotated()
    {
        var ctx = Ctx();
        var entry = Entry();
        Write(Path.Combine(Saves(ctx), "good.sav"));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 1) { Now = Clock(new DateTime(2026, 1, 1, 10, 0, 0)) };

        var locked = Path.Combine(Saves(ctx), "locked.sav");
        Write(locked);
        string incomplete;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = svc.Backup(Game, SaveLocator.Scan(entry, ctx));
            Assert.False(r.Complete);
            Assert.EndsWith("_unvollstaendig.zip", r.Id);
            incomplete = r.Id!;
        }
        File.Delete(locked);

        svc.Backup(Game, SaveLocator.Scan(entry, ctx));
        svc.Backup(Game, SaveLocator.Scan(entry, ctx));

        var list = svc.List(Game);
        Assert.Single(list, b => b.Complete);            // limit 1 counts only complete ones
        Assert.Contains(list, b => b.Id == incomplete && !b.Complete); // and never touches the incomplete one
    }

    [Fact]
    public void BackupWithFarFewerFilesThanBeforeIsIncomplete()
    {
        var ctx = Ctx();
        var entry = Entry();
        for (var i = 0; i < 6; i++) Write(Path.Combine(Saves(ctx), $"s{i}.sav"));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5) { Now = Clock(new DateTime(2026, 1, 1, 10, 0, 0)) };
        Assert.True(svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Complete);

        for (var i = 1; i < 6; i++) File.Delete(Path.Combine(Saves(ctx), $"s{i}.sav"));
        var r = svc.Backup(Game, SaveLocator.Scan(entry, ctx));
        Assert.False(r.Complete);
        Assert.Contains(r.Errors, e => e.Contains("letzte Sicherung"));
        Assert.Contains("_unvollstaendig", r.Id);
    }

    [Fact]
    public void IncompleteFlagIsWrittenIntoTheManifestToo()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5);
        var scan = SaveLocator.Scan(Entry(), ctx) with { Errors = ["boom"] };
        var id = svc.Backup(Game, scan).Id!;
        using var zip = ZipFile.OpenRead(Path.Combine(GameDir, id));
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        Assert.Contains("\"complete\": false", reader.ReadToEnd());
    }

    // --- M4: ZIP timestamps, generic cleanup ---

    [Fact]
    public void ZipTimeIsClampedToTheSupportedRange()
    {
        Assert.Equal(1980, SaveBackupService.ZipTime(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Year);
        Assert.Equal(2107, SaveBackupService.ZipTime(new DateTime(2500, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Year);
    }

    [Fact]
    public void FilesWithAncientDatesAreBackedUpAndRestored()
    {
        var ctx = Ctx();
        var entry = Entry();
        var file = Path.Combine(Saves(ctx), "old.sav");
        Write(file, "old");
        File.SetLastWriteTimeUtc(file, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5) { Now = Clock(new DateTime(2026, 1, 1, 10, 0, 0)) };

        var r = svc.Backup(Game, SaveLocator.Scan(entry, ctx));
        Assert.True(r.Complete);
        File.WriteAllText(file, "new");
        // The safety backup of the ancient file must not break the restore either.
        var restored = svc.Restore(Game, r.Id!, entry, ctx);
        Assert.Equal(1, restored.Restored);
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(GameDir, "*.part"));
    }

    // --- M5: matching ---

    private const string Names = """
        Half-Life:
          steam:
            id: 70
        Half Life:
          installDir:
            HL: {}
        "Half-Life 2":
          steam:
            id: 220
        Shenmue III:
          installDir:
            Shenmue3: {}
        "Wolfenstein: 3D":
          installDir:
            Wolf3D: {}
        Wolfenstein 3D:
          installDir:
            Wolf3D: {}
        Some GOG Game:
          gog:
            id: 555
        """;

    [Fact]
    public void ExactNameBeatsNormalisedAndAmbiguityMeansNoMatch()
    {
        var index = ManifestIndex.Load(new StringReader(Names));
        Assert.Equal("Half-Life", index.Find(null, "Half-Life")!.Name);
        Assert.Equal("Half Life", index.Find(null, "half life")!.Name);
        Assert.Null(index.Find(null, "HalfLife"));            // "Half-Life" and "Half Life" both normalise to it
        Assert.Equal("Half-Life 2", index.Find(null, "Half Life 2")!.Name); // unique normalised name
        Assert.Equal("Wolfenstein 3D", index.Find(null, "Wolfenstein 3D")!.Name);
        Assert.Null(index.Find(null, "Wolfenstein3D"));       // two candidates
    }

    [Fact]
    public void InstallDirIsOnlyAUniqueFallback()
    {
        var index = ManifestIndex.Load(new StringReader(Names));
        Assert.Equal("Shenmue III", index.Find(null, "Shenmue3")!.Name);
        Assert.Null(index.Find(null, "Wolf3D"));              // shared by two games
        Assert.Equal("Half Life", index.Find(null, "HL")!.Name);
    }

    [Fact]
    public void SteamAndGogIdsWinOverNames()
    {
        var index = ManifestIndex.Load(new StringReader(Names));
        Assert.Equal("Half-Life", index.Find(70, "Anything")!.Name);
        Assert.Equal("Some GOG Game", index.Find(null, "Whatever", 555)!.Name);
        Assert.Equal("Half-Life", index.Find(70, "Shenmue III")!.Name);
    }

    // --- M6: restore safety ---

    [Fact]
    public void ReadOnlySaveFileIsRestoredAndKeepsItsAttribute()
    {
        var ctx = Ctx();
        var entry = Entry();
        var file = Path.Combine(Saves(ctx), "ro.sav");
        Write(file, "original");
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5) { Now = Clock(new DateTime(2026, 1, 1, 10, 0, 0)) };
        var id = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;

        File.WriteAllText(file, "changed");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var r = svc.Restore(Game, id, entry, ctx);

        Assert.Equal(1, r.Restored);
        Assert.Equal("original", File.ReadAllText(file));
        Assert.True((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public void LockedTargetAbortsBeforeAnythingChanges()
    {
        var ctx = Ctx();
        var entry = Entry();
        var a = Path.Combine(Saves(ctx), "a.sav");
        var b = Path.Combine(Saves(ctx), "b.sav");
        Write(a, "a0");
        Write(b, "b0");
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5) { Now = Clock(new DateTime(2026, 1, 1, 10, 0, 0)) };
        var id = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;
        File.WriteAllText(a, "a1");
        File.WriteAllText(b, "b1");

        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => svc.Restore(Game, id, entry, ctx));
        }
        Assert.Equal("a1", File.ReadAllText(a));
        Assert.Equal("b1", File.ReadAllText(b));
        Assert.Single(svc.List(Game)); // no safety backup was even taken
    }

    [Fact]
    public void TargetThatIsAFolderAbortsBeforeAnythingChanges()
    {
        var ctx = Ctx();
        var entry = Entry();
        Write(Path.Combine(Saves(ctx), "a.sav"), "a0");
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5);
        var id = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;
        File.Delete(Path.Combine(Saves(ctx), "a.sav"));
        Directory.CreateDirectory(Path.Combine(Saves(ctx), "a.sav"));
        Assert.Throws<IOException>(() => svc.Restore(Game, id, entry, ctx));
    }

    [Fact]
    public void FailureHalfwayRollsBackToTheSafetyBackup()
    {
        var ctx = Ctx();
        var entry = Entry();
        var a = Path.Combine(Saves(ctx), "a.sav");
        var b = Path.Combine(Saves(ctx), "b.sav");
        var c = Path.Combine(Saves(ctx), "c.sav");
        Write(a, "a0");
        Write(b, "b0");
        Write(c, "c0");
        var clock = Clock(new DateTime(2026, 1, 1, 10, 0, 0));
        var plain = new SaveBackupService(Path.Combine(_tmp, "backups"), 5) { Now = clock };
        var id = plain.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;

        File.WriteAllText(a, "a1");
        File.WriteAllText(b, "b1");
        File.Delete(c); // the restore would create it again

        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5)
        {
            Now = clock,
            BeforeWrite = target =>
            {
                if (target.EndsWith("b.sav")) throw new IOException("disk says no");
            }
        };
        var r = svc.Restore(Game, id, entry, ctx);

        Assert.True(r.RolledBack);
        Assert.Equal(0, r.Restored);
        Assert.Contains(r.Errors, e => e.Contains("disk says no"));
        Assert.Equal("a1", File.ReadAllText(a));
        Assert.Equal("b1", File.ReadAllText(b));
        Assert.False(File.Exists(c));
    }

    [Fact]
    public void RestoreAbortsWhenTheSafetyBackupIsIncomplete()
    {
        var ctx = Ctx();
        var entry = Entry();
        var a = Path.Combine(Saves(ctx), "a.sav");
        Write(a, "a0");
        var clock = Clock(new DateTime(2026, 1, 1, 10, 0, 0));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5)
        {
            Now = clock,
            SafetyScan = (e, c) => SaveLocator.Scan(e, c) with { Truncated = true, Errors = ["gekappt"] }
        };
        var id = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;
        File.WriteAllText(a, "a1");

        var ex = Assert.Throws<IOException>(() => svc.Restore(Game, id, entry, ctx));
        Assert.Contains("nicht vollständig", ex.Message);
        Assert.Equal("a1", File.ReadAllText(a));
    }

    [Fact]
    public void RestoreLeavesFilesThatAreNotInTheBackup()
    {
        var ctx = Ctx();
        var entry = Entry();
        Write(Path.Combine(Saves(ctx), "a.sav"), "a0");
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5) { Now = Clock(new DateTime(2026, 1, 1, 10, 0, 0)) };
        var id = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;
        Write(Path.Combine(Saves(ctx), "new.sav"), "n");
        svc.Restore(Game, id, entry, ctx);
        Assert.True(File.Exists(Path.Combine(Saves(ctx), "new.sav")));
    }

    // --- M7 / N9: root protection ---

    [Fact]
    public void PatternsDirectlyBelowProfileRootsOrInWindowsAreDropped()
    {
        var ctx = Ctx(Path.Combine(_tmp, "g"));
        var entry = SaveEntry(
            "<home>/*.sav", "<winAppData>/*", "<winDocuments>/**", "<winLocalAppData>/Game/*.sav", "<winDir>/System32/x.ini",
            "<winProgramData>/Game", "C:/Users/*/x", "C:/save.dat", "<winPublic>/x");
        var patterns = SaveLocator.ResolvePatterns(entry, ctx);
        Assert.Equal(3, patterns.Count);
        Assert.Contains(patterns, p => p.EndsWith(@"Local\Game\*.sav"));
        Assert.Contains(patterns, p => p.EndsWith(@"pd\Game"));
    }

    [Fact]
    public void BaseIsNotUsedWhenTheInstallDirIsAProfileOrDriveRoot()
    {
        var entry = SaveEntry("<base>/saves/*.sav");
        Assert.Empty(SaveLocator.ResolvePatterns(entry, Ctx(@"C:\")));
        Assert.Empty(SaveLocator.ResolvePatterns(entry, Ctx(Ctx().Home)));
        Assert.Empty(SaveLocator.ResolvePatterns(entry, Ctx(Ctx().WinDocuments)));
        Assert.Single(SaveLocator.ResolvePatterns(entry, Ctx(Path.Combine(_tmp, "games", "X"))));
    }

    [Fact]
    public void RootAndStoreUserIdOnlyExistForSteam()
    {
        var install = Path.Combine(_tmp, "lib", "steamapps", "common", "G");
        var entry = SaveEntry("<root>/userdata/<storeUserId>/1/remote", "<root>/saves");
        var steam = Ctx(install) with { SteamRoot = Path.Combine(_tmp, "steam") };
        Assert.NotEmpty(SaveLocator.ResolvePatterns(entry, steam));
        Assert.Empty(SaveLocator.ResolvePatterns(entry, steam with { Store = "gog" }));
        Assert.Empty(SaveLocator.ResolvePatterns(entry, steam with { Store = null }));
    }

    [Fact]
    public void TooManyFilesIsReportedNotSilent()
    {
        var ctx = Ctx();
        for (var i = 0; i < 5; i++) Write(Path.Combine(Saves(ctx), $"s{i}.sav"));
        var scan = SaveLocator.Scan(Entry(), ctx, maxFiles: 3);
        Assert.True(scan.Truncated);
        Assert.Equal(3, scan.Files.Count);
        Assert.Contains(scan.Errors, e => e.Contains("Mehr als 3"));

        var r = new SaveBackupService(Path.Combine(_tmp, "backups"), 5).Backup(Game, scan);
        Assert.False(r.Complete);
    }

    [Fact]
    public void LeftoverTempFilesAreNotPartOfTheSaves()
    {
        var ctx = Ctx();
        var entry = SaveEntry("<winAppData>/RealGame/saves");
        Write(Path.Combine(Saves(ctx), "a.sav"));
        Write(Path.Combine(Saves(ctx), "a.sav" + SaveLocator.TempSuffix));
        Assert.Single(SaveLocator.Scan(entry, ctx).Files);
    }

    // --- M8: absolute paths and second Steam library ---

    [Fact]
    public void FilesOutsideKnownFoldersUseTheAbsoluteTokenAndRestore()
    {
        var ctx = Ctx();
        var dir = Path.Combine(_tmp, "games", "X", "saves");
        var entry = SaveEntry(Path.Combine(dir, "*.sav"));
        Write(Path.Combine(dir, "one.sav"), "1");
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5);

        var scan = SaveLocator.Scan(entry, ctx);
        Assert.Equal("abs", Assert.Single(scan.Files).Root);
        Assert.Empty(scan.Errors);
        var id = svc.Backup(Game, scan).Id!;
        File.WriteAllText(Path.Combine(dir, "one.sav"), "2");
        Assert.Equal(1, svc.Restore(Game, id, entry, ctx).Restored);
        Assert.Equal("1", File.ReadAllText(Path.Combine(dir, "one.sav")));
    }

    [Fact]
    public void AbsoluteRestoreStillObeysTheWhitelist()
    {
        var ctx = Ctx();
        var entry = SaveEntry(Path.Combine(_tmp, "games", "X", "saves", "*.sav"));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5);
        var dir = svc.GameDirectory(Game);
        Directory.CreateDirectory(dir);
        var drive = char.ToUpperInvariant(_tmp[0]);
        using (var zip = ZipFile.Open(Path.Combine(dir, "2026-01-01_00-00.zip"), ZipArchiveMode.Create))
        {
            var rel = $"{drive}/Windows/evil.sav";
            using (var w = new StreamWriter(zip.CreateEntry("files/abs/" + rel).Open())) w.Write("x");
            using var m = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            m.Write($$"""{"version":1,"game":"x","createdUtc":"2026-01-01T00:00:00Z","entries":[{"zip":"files/abs/{{rel}}","root":"abs","rel":"{{rel}}","size":1,"modifiedUtc":"2026-01-01T00:00:00Z"}]}""");
        }
        Assert.Throws<InvalidDataException>(() => svc.Restore(Game, "2026-01-01_00-00.zip", entry, ctx));
    }

    [Fact]
    public void SecondSteamLibraryIsARootOfItsOwn()
    {
        var install = Path.Combine(_tmp, "lib2", "steamapps", "common", "G");
        var ctx = Ctx(install) with { SteamRoot = Path.Combine(_tmp, "steam") };
        var entry = SaveEntry("<root>/saves/*.sav");
        Write(Path.Combine(_tmp, "lib2", "saves", "x.sav"));
        var scan = SaveLocator.Scan(entry, ctx);
        var file = Assert.Single(scan.Files);
        Assert.Equal("steamLibrary", file.Root);

        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5);
        var id = svc.Backup(Game, scan).Id!;
        File.Delete(file.FullPath);
        Assert.Equal(1, svc.Restore(Game, id, entry, ctx).Restored);
        Assert.True(File.Exists(file.FullPath));
    }

    // --- Folders bound to the item id, rotation by manifest time ---

    [Fact]
    public void BackupFoldersFollowTheItemIdNotTheName()
    {
        var a = new GameRef("Same Name", "steam:1");
        var b = new GameRef("Same Name", "epic:xyz");
        Assert.NotEqual(SaveBackupService.FolderName(a), SaveBackupService.FolderName(b));

        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5);
        svc.Backup(a, SaveLocator.Scan(Entry(), ctx));
        Assert.Single(svc.List(a));
        Assert.Empty(svc.List(b));
    }

    [Fact]
    public void LegacyNameFolderIsMigratedOnce()
    {
        var root = Path.Combine(_tmp, "backups");
        Directory.CreateDirectory(Path.Combine(root, "Real Game"));
        File.WriteAllText(Path.Combine(root, "Real Game", "2025-01-01_00-00.zip"), "x");
        var svc = new SaveBackupService(root, 5);
        svc.MigrateLegacy(Game);
        Assert.False(Directory.Exists(Path.Combine(root, "Real Game")));
        Assert.True(File.Exists(Path.Combine(GameDir, "2025-01-01_00-00.zip")));
        svc.MigrateLegacy(Game); // second call changes nothing
    }

    [Fact]
    public void RotationUsesTheTimeInsideTheBackupNotTheFileTime()
    {
        var ctx = Ctx();
        var entry = Entry();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var times = new Queue<DateTime>([
            new(2026, 1, 1, 12, 5, 0), new(2026, 1, 1, 12, 5, 0),   // created + name for the first backup
            new(2026, 1, 1, 12, 1, 0), new(2026, 1, 1, 12, 1, 0),
            new(2026, 1, 1, 12, 3, 0), new(2026, 1, 1, 12, 3, 0)]);
        DateTime last = default;
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 2) { Now = () => times.Count > 0 ? last = times.Dequeue() : last };
        for (var i = 0; i < 3; i++) svc.Backup(Game, SaveLocator.Scan(entry, ctx));

        var left = svc.List(Game).Select(b => b.Id).ToList();
        Assert.Equal(["2026-01-01_12-05.zip", "2026-01-01_12-03.zip"], left);
    }

    // --- Service level: running games, locking, cache ---

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }

    private (SaveGameService Svc, LibraryItem Item, SaveContext Ctx) Service(
        Func<LibraryItem, bool>? running = null, Func<string, int>? count = null, string? manifestText = null, DateTimeOffset? start = null)
    {
        var data = Path.Combine(_tmp, "data");
        Directory.CreateDirectory(Path.Combine(data, "saves"));
        if (manifestText is not null) File.WriteAllText(Path.Combine(data, "saves", "manifest.yaml"), manifestText);
        var config = new ConfigStore(data);
        var item = new LibraryItem
        {
            Id = "steam:4242", Source = "steam", Name = "Real Game", Launch = new LaunchTarget(LaunchKind.Url, "steam://run/4242"),
            InstallDir = Path.Combine(_tmp, "steamlib", "steamapps", "common", "RealGame")
        };
        var now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var svc = new SaveGameService(config, id => id == item.Id ? item : null, count ?? (_ => 1),
            new ManifestStore(new HttpClient(new Offline()), Path.Combine(data, "saves")), data, () => null, running ?? (_ => false))
        {
            Now = () => now
        };
        return (svc, item, Ctx(item.InstallDir));
    }

    private const string ServiceManifest = """
        Real Game:
          files:
            <base>/saves/*.sav:
              tags:
                - save
          steam:
            id: 4242
        """;

    [Fact]
    public async Task BackupAndRestoreAreBlockedWhileTheGameRuns()
    {
        var (svc, item, _) = Service(running: _ => true, manifestText: ServiceManifest);
        Write(Path.Combine(item.InstallDir!, "saves", "a.sav"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BackupAsync(item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(item.Id, "2026-01-01_00-00.zip"));
    }

    [Fact]
    public void ProcessesRunningFromTheInstallFolderCountAsRunning()
    {
        var dir = @"D:\Games\X";
        Assert.True(SaveGameService.AnyProcessUnder([(1, @"D:\Games\X\bin\game.exe")], dir));
        Assert.True(SaveGameService.AnyProcessUnder([(1, @"d:\games\x\GAME.EXE")], dir + @"\"));
        Assert.False(SaveGameService.AnyProcessUnder([(1, @"D:\Games\X2\game.exe"), (2, null), (3, @"C:\Windows\x.exe")], dir));
        Assert.False(SaveGameService.AnyProcessUnder([(1, @"D:\Games\X\game.exe")], null));
    }

    [Fact]
    public async Task OnlyOneBackupOrRestorePerGameAtATime()
    {
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        var first = true;
        var (svc, item, _) = Service(manifestText: ServiceManifest, running: _ =>
        {
            if (!first) return false;
            first = false;
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return false;
        });
        Write(Path.Combine(item.InstallDir!, "saves", "a.sav"));

        var one = svc.BackupAsync(item.Id);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BackupAsync(item.Id));
        Assert.Contains("schon", ex.Message);
        gate.Set();
        Assert.NotNull((await one).Id);
        // Free again afterwards.
        Assert.NotNull((await svc.BackupAsync(item.Id)).Id);
    }

    [Fact]
    public async Task InfoScanIsCachedForAMinuteAndDroppedAfterBackup()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (svc, item, _) = Service(manifestText: ServiceManifest, start: now);
        var saves = Path.Combine(item.InstallDir!, "saves");
        Write(Path.Combine(saves, "a.sav"));
        Assert.Equal(1, (await svc.GetInfoAsync(item.Id)).FileCount);

        Write(Path.Combine(saves, "b.sav"));
        Assert.Equal(1, (await svc.GetInfoAsync(item.Id)).FileCount); // cached

        await svc.BackupAsync(item.Id);
        Assert.Equal(2, (await svc.GetInfoAsync(item.Id)).FileCount);  // backup dropped the cache
    }

    [Fact]
    public async Task InfoScanCacheExpires()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var data = Path.Combine(_tmp, "data");
        var (_, item, _) = Service(manifestText: ServiceManifest);
        var config = new ConfigStore(data);
        var svc = new SaveGameService(config, id => item, _ => 1, new ManifestStore(new HttpClient(new Offline()), Path.Combine(data, "saves")),
            data, () => null, _ => false) { Now = () => clock };
        var saves = Path.Combine(item.InstallDir!, "saves");
        Write(Path.Combine(saves, "a.sav"));
        Assert.Equal(1, (await svc.GetInfoAsync(item.Id)).FileCount);
        Write(Path.Combine(saves, "b.sav"));
        clock = clock.Add(SaveGameService.ScanCacheTtl).AddSeconds(1);
        Assert.Equal(2, (await svc.GetInfoAsync(item.Id)).FileCount);
    }

    [Fact]
    public async Task OfflineWithoutManifestIsReportedAsUnavailable()
    {
        var (svc, item, _) = Service();
        var info = await svc.GetInfoAsync(item.Id);
        Assert.False(info.Supported);
        Assert.True(info.Unavailable);
    }

    [Fact]
    public async Task LegacyFolderIsOnlyMigratedWhenTheNameIsUnique()
    {
        var unique = Service(manifestText: ServiceManifest);
        var legacy = Path.Combine(unique.Svc.BackupDir, "Real Game");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "2025-01-01_00-00.zip"), "x");
        await unique.Svc.GetInfoAsync(unique.Item.Id);
        Assert.False(Directory.Exists(legacy));

        var data2 = Path.Combine(_tmp, "other");
        Directory.CreateDirectory(Path.Combine(data2, "saves"));
        File.WriteAllText(Path.Combine(data2, "saves", "manifest.yaml"), ServiceManifest);
        var svc2 = new SaveGameService(new ConfigStore(data2), _ => unique.Item, _ => 2,
            new ManifestStore(new HttpClient(new Offline()), Path.Combine(data2, "saves")), data2, () => null, _ => false);
        var legacy2 = Path.Combine(svc2.BackupDir, "Real Game");
        Directory.CreateDirectory(legacy2);
        await svc2.GetInfoAsync(unique.Item.Id);
        Assert.True(Directory.Exists(legacy2)); // ambiguous: left alone
    }
}
