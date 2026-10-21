using System.IO.Compression;
using System.Text.Json;
using CouchHud.Core.Saves;

namespace CouchHud.Core.Tests;

public sealed class SaveGameTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "couchhud-saves-" + Guid.NewGuid().ToString("N"));

    public SaveGameTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly GameRef Game = new("Real Game", "steam:4242");
    private string GameDir => Path.Combine(_tmp, "backups", "Real Game [steam_4242]");

    private const string Fixture = """
        ---
        "! Quoted: Game !":
          alias: Real Game
        "Real Game":
          files:
            "<winAppData>/RealGame/saves/**/*.sav":
              tags:
                - save
              when:
                - os: windows
            "<winLocalAppData>/RealGame/settings.ini":
              tags:
                - config
            "<home>/.config/realgame":
              tags:
                - save
              when:
                - os: linux
            "<base>/userdata/<storeUserId>/slot.dat":
              tags:
                - save
              when:
                - os: windows
                  store: steam
            "<winDir>/x.ini":
              tags:
                - save
          installDir:
            RealGame: {}
          registry:
            HKEY_CURRENT_USER/Software/RealGame:
              tags:
                - save
          steam:
            id: 4242
        $1 Ride:
          steam:
            id: 99
          id:
            steamExtra:
              - 100
              - 101
        "Other: Name":
          files:
            <winDocuments>/My Games/Other/*.sav: {}
        """;

    private ManifestIndex Index() => ManifestIndex.Load(new StringReader(Fixture));

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
        SteamUserIds = ["111", "222"]
    };

    private void Write(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // --- Manifest ---

    [Fact]
    public void ParsesEntriesWithQuotesAndFlowForms()
    {
        var index = Index();
        Assert.Equal(4, index.Count);
        var entry = index.Find(4242, "whatever")!;
        Assert.Equal("Real Game", entry.Name);
        Assert.Equal("RealGame", entry.InstallDir);
        Assert.Equal(5, entry.Files.Count);
        Assert.Equal(["save"], entry.Files[0].Tags);
        Assert.Equal("windows", entry.Files[0].When[0].Os);
        Assert.Equal("steam", entry.Files[3].When[0].Store);
        Assert.Single(entry.Registry);
    }

    [Fact]
    public void MatchesBySteamIdThenName()
    {
        var index = Index();
        Assert.Equal("$1 Ride", index.Find(100, "x")!.Name);
        Assert.Equal("$1 Ride", index.Find(null, "$1 Ride")!.Name);
        Assert.Equal("Real Game", index.Find(null, "REAL game")!.Name);
        Assert.Equal("Real Game", index.Find(null, "RealGame")!.Name); // via installDir
        Assert.Null(index.Find(null, "Unknown"));
        // Steam id wins over a wrong name.
        Assert.Equal("Real Game", index.Find(4242, "Other: Name")!.Name);
    }

    [Fact]
    public void FollowsAliases()
    {
        Assert.Equal("Real Game", Index().Find(null, "! Quoted: Game !")!.Name);
    }

    [Fact]
    public void UnquotedKeyWithPlaceholderParses()
    {
        var other = Index().Find(null, "Other: Name")!;
        Assert.Equal("<winDocuments>/My Games/Other/*.sav", other.Files[0].Path);
    }

    // --- Placeholders ---

    [Fact]
    public void ResolvesPlaceholdersForWindowsOnly()
    {
        var entry = Index().Find(4242, "")!;
        var install = Path.Combine(_tmp, "steam", "steamapps", "common", "RealGame");
        var patterns = SaveLocator.ResolvePatterns(entry, Ctx(install) with { WinDir = @"C:Windows" });

        Assert.Contains(Path.Combine(_tmp, "home", "AppData", "Roaming", "RealGame", "saves", "**", "*.sav"), patterns);
        // <storeUserId> multiplies, <base> is the install dir.
        Assert.Contains(Path.Combine(install, "userdata", "111", "slot.dat"), patterns);
        Assert.Contains(Path.Combine(install, "userdata", "222", "slot.dat"), patterns);
        // Linux path, config-only path and too-shallow <winDir> path are out.
        Assert.DoesNotContain(patterns, p => p.Contains(".config"));
        Assert.DoesNotContain(patterns, p => p.EndsWith("settings.ini"));
        Assert.DoesNotContain(patterns, p => p.EndsWith("x.ini"));
    }

    [Fact]
    public void ConfigTagOnlyWhenRequested()
    {
        var entry = Index().Find(4242, "")!;
        var patterns = SaveLocator.ResolvePatterns(entry, Ctx() with { IncludeConfig = true });
        Assert.Contains(patterns, p => p.EndsWith("settings.ini"));
    }

    [Fact]
    public void BasePathsNeedAnInstallDir()
    {
        var entry = Index().Find(4242, "")!;
        Assert.DoesNotContain(SaveLocator.ResolvePatterns(entry, Ctx()), p => p.EndsWith("slot.dat"));
    }

    [Fact]
    public void StoreConditionFilters()
    {
        var entry = Index().Find(4242, "")!;
        var install = Path.Combine(_tmp, "g");
        Assert.DoesNotContain(SaveLocator.ResolvePatterns(entry, Ctx(install) with { Store = "gog" }), p => p.EndsWith("slot.dat"));
        Assert.Contains(SaveLocator.ResolvePatterns(entry, Ctx(install) with { Store = "steam" }), p => p.EndsWith("slot.dat"));
    }

    [Fact]
    public void SteamLibraryRootIsDerivedFromInstallDir()
    {
        Assert.Equal(@"D:\Lib", SaveLocator.LibraryRootOf(@"D:\Lib\steamapps\common\Game"));
        Assert.Null(SaveLocator.LibraryRootOf(@"D:\Games\Game"));
    }

    // --- Wildcards ---

    [Fact]
    public void GlobHandlesStarAndDoubleStar()
    {
        var root = Path.Combine(_tmp, "g");
        Write(Path.Combine(root, "a.sav"));
        Write(Path.Combine(root, "b.txt"));
        Write(Path.Combine(root, "sub", "c.sav"));
        Write(Path.Combine(root, "sub", "deep", "d.sav"));

        Assert.Equal(["a.sav"], SaveLocator.Glob(Path.Combine(root, "*.sav")).Select(Path.GetFileName));
        var all = SaveLocator.Glob(Path.Combine(root, "**", "*.sav")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["a.sav", "c.sav", "d.sav"], all);
        Assert.Equal(["c.sav"], SaveLocator.Glob(Path.Combine(root, "s?b", "*.sav")).Select(Path.GetFileName));
    }

    [Fact]
    public void ScanMapsFilesToPlaceholderRootsAndFolderMatchesRecurse()
    {
        var ctx = Ctx();
        Write(Path.Combine(ctx.WinAppData, "RealGame", "saves", "x", "1.sav"), "123");
        Write(Path.Combine(ctx.WinAppData, "RealGame", "saves", "2.sav"), "45");
        Write(Path.Combine(ctx.WinAppData, "RealGame", "saves", "ignore.txt"));
        var scan = SaveLocator.Scan(Index().Find(4242, "")!, ctx);

        Assert.Equal(2, scan.Files.Count);
        Assert.Equal(5, scan.TotalBytes);
        Assert.All(scan.Files, f => Assert.Equal("winAppData", f.Root));
        Assert.Contains(scan.Files, f => f.Rel == "RealGame/saves/x/1.sav");
        Assert.Single(scan.RegistryKeys);
    }

    [Fact]
    public void FolderPatternIncludesEverythingBelow()
    {
        var entry = new ManifestEntry("F") { Files = [new ManifestPath("<winDocuments>/My Games/F", [], [])] };
        var ctx = Ctx();
        Write(Path.Combine(ctx.WinDocuments, "My Games", "F", "a", "b.dat"));
        Assert.Single(SaveLocator.Scan(entry, ctx).Files);
    }

    // --- Backup / restore ---

    private (SaveBackupService Svc, ManifestEntry Entry, SaveContext Ctx) Setup(int keep = 5)
    {
        var ctx = Ctx();
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), keep);
        return (svc, Index().Find(4242, "")!, ctx);
    }

    [Fact]
    public void BackupRestoreRoundtrip()
    {
        var (svc, entry, ctx) = Setup();
        var save = Path.Combine(ctx.WinAppData, "RealGame", "saves", "slot1.sav");
        Write(save, "original");

        var result = svc.Backup(Game, SaveLocator.Scan(entry, ctx));
        Assert.NotNull(result.Id);
        Assert.Equal(1, result.FileCount);
        Assert.Empty(result.Errors);
        Assert.Matches(@"^\d{4}-\d\d-\d\d_\d\d-\d\d\.zip$", result.Id);
        Assert.Empty(Directory.GetFiles(GameDir, "*.part"));

        File.WriteAllText(save, "changed");
        var restored = svc.Restore(Game, result.Id!, entry, ctx);
        Assert.Equal(1, restored.Restored);
        Assert.Empty(restored.Errors);
        Assert.Equal("original", File.ReadAllText(save));
        // The "changed" state was kept as safety backup.
        Assert.NotNull(restored.SafetyBackupId);
        Assert.Contains(svc.List(Game), b => b.Safety && b.Id == restored.SafetyBackupId);
    }

    [Fact]
    public void RestoreCreatesMissingFoldersAndSurvivesMovedUserProfile()
    {
        var (svc, entry, ctx) = Setup();
        Write(Path.Combine(ctx.WinAppData, "RealGame", "saves", "s.sav"), "abc");
        var id = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;
        Directory.Delete(Path.Combine(ctx.WinAppData, "RealGame"), true);

        // Different profile folder, same placeholder structure.
        var moved = ctx with
        {
            Home = Path.Combine(_tmp, "home2"),
            WinAppData = Path.Combine(_tmp, "home2", "AppData", "Roaming")
        };
        var r = svc.Restore(Game, id, entry, moved);
        Assert.Equal(1, r.Restored);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(moved.WinAppData, "RealGame", "saves", "s.sav")));
    }

    [Fact]
    public void BackupReportsUnreadableFilesWithoutAborting()
    {
        var (svc, entry, ctx) = Setup();
        var good = Path.Combine(ctx.WinAppData, "RealGame", "saves", "good.sav");
        var locked = Path.Combine(ctx.WinAppData, "RealGame", "saves", "locked.sav");
        Write(good);
        Write(locked);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = svc.Backup(Game, SaveLocator.Scan(entry, ctx));
        Assert.Equal(1, result.FileCount);
        Assert.Single(result.Errors);
        Assert.Contains("locked.sav", result.Errors[0]);
    }

    [Fact]
    public void BackupWithoutSavesIsAnError()
    {
        var (svc, entry, ctx) = Setup();
        var result = svc.Backup(Game, SaveLocator.Scan(entry, ctx));
        Assert.Null(result.Id);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void RotationKeepsNewestBackups()
    {
        var (_, entry, ctx) = Setup();
        Write(Path.Combine(ctx.WinAppData, "RealGame", "saves", "s.sav"));
        var clock = new DateTime(2026, 1, 1, 12, 0, 0);
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 3) { Now = () => clock };
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            clock = clock.AddMinutes(1);
            ids.Add(svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!);
        }
        var left = svc.List(Game).Select(b => b.Id).ToList();
        Assert.Equal(3, left.Count);
        Assert.Equal(ids.AsEnumerable().Reverse().Take(3), left);
    }

    [Fact]
    public void SameMinuteBackupsDontOverwrite()
    {
        var (_, entry, ctx) = Setup();
        Write(Path.Combine(ctx.WinAppData, "RealGame", "saves", "s.sav"));
        var svc = new SaveBackupService(Path.Combine(_tmp, "backups"), 5) { Now = () => new DateTime(2026, 1, 1, 12, 0, 0) };
        var a = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id;
        var b = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id;
        Assert.NotEqual(a, b);
    }

    // --- Zip-Slip ---

    private string Forge(SaveBackupService svc, params (string Root, string Rel)[] files)
    {
        var dir = svc.GameDirectory(Game);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "2026-01-01_00-00.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entries = new List<object>();
        foreach (var (root, rel) in files)
        {
            var name = $"files/{root}/{rel}";
            using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write("evil");
            entries.Add(new { zip = name, root, rel, size = 4, modifiedUtc = DateTime.UtcNow });
        }
        using var m = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        m.Write(JsonSerializer.Serialize(new { version = 1, game = "Real Game", createdUtc = DateTime.UtcNow, entries }));
        return Path.GetFileName(path);
    }

    [Fact]
    public void ZipSlipIsRejectedPerFile()
    {
        var (svc, entry, ctx) = Setup();
        var outside = Path.Combine(_tmp, "evil.txt");
        var id = Forge(svc,
            ("winAppData", "../../../evil.txt"),
            ("winAppData", "RealGame/saves/../../../../evil.txt"),
            ("winAppData", "C:/evil.txt"),
            ("winAppData", "RealGame/saves/ok.sav"));

        var r = svc.Restore(Game, id, entry, ctx);

        Assert.False(File.Exists(outside));
        Assert.Equal(1, r.Restored);
        Assert.Equal(3, r.Errors.Count);
        Assert.Equal("evil", File.ReadAllText(Path.Combine(ctx.WinAppData, "RealGame", "saves", "ok.sav")));
    }

    [Fact]
    public void RestoreOnlyWritesToSaveLocations()
    {
        var (svc, entry, ctx) = Setup();
        // Inside the profile, but not a save location of this game (think autostart folder).
        var id = Forge(svc, ("winAppData", "Microsoft/Windows/Startup/run.bat"), ("home", "Desktop/x.txt"), ("bogus", "a.sav"));
        var ex = Assert.Throws<InvalidDataException>(() => svc.Restore(Game, id, entry, ctx));
        Assert.Contains("nichts", ex.Message);
        Assert.False(File.Exists(Path.Combine(ctx.WinAppData, "Microsoft", "Windows", "Startup", "run.bat")));
    }

    [Fact]
    public void BackupIdsCannotEscapeTheBackupFolder()
    {
        var (svc, entry, ctx) = Setup();
        Assert.Throws<ArgumentException>(() => svc.Restore(Game, @"..\..\x.zip", entry, ctx));
        Assert.False(SaveBackupService.IsValidId("a/b.zip"));
        Assert.True(SaveBackupService.IsValidId("2026-01-01_12-00_vorher-2.zip"));
        Assert.False(svc.Delete(Game, @"..\x.zip"));
    }

    [Fact]
    public void AllowedTargetMatching()
    {
        var p = new[] { @"C:\Users\u\AppData\Roaming\G\**\*.sav", @"C:\Users\u\Documents\G" };
        Assert.True(SaveLocator.IsAllowedTarget(@"C:\Users\u\AppData\Roaming\G\a.sav", p));
        Assert.True(SaveLocator.IsAllowedTarget(@"C:\Users\u\AppData\Roaming\G\x\y\a.sav", p));
        Assert.False(SaveLocator.IsAllowedTarget(@"C:\Users\u\AppData\Roaming\G\a.exe", p));
        Assert.True(SaveLocator.IsAllowedTarget(@"C:\Users\u\Documents\G\any\thing.bin", p));
        Assert.False(SaveLocator.IsAllowedTarget(@"C:\Users\u\Documents\Other\a.bin", p));
        Assert.False(SaveLocator.IsAllowedTarget(@"C:\Users\u\Documents\G\..\..\x", p));
    }

    // --- Manifest download ---

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task ManifestIsCachedUpdatedWeeklyAndUsesEtag()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var handler = new FakeHandler(req =>
        {
            if (req.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"")) return new HttpResponseMessage(System.Net.HttpStatusCode.NotModified);
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Fixture) };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return r;
        });
        var store = new ManifestStore(new HttpClient(handler), Path.Combine(_tmp, "m")) { Now = () => now };

        Assert.NotNull(await store.GetAsync());
        Assert.Single(handler.Requests);

        now = now.AddDays(2);
        Assert.NotNull(await store.GetAsync());
        Assert.Single(handler.Requests); // not due yet

        now = now.AddDays(6);
        Assert.NotNull(await store.GetAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("\"v1\"", handler.Requests[1].Headers.IfNoneMatch.Single().Tag);
    }

    [Fact]
    public async Task OfflineKeepsOldManifestAndFreshInstallGetsNull()
    {
        var offline = new FakeHandler(_ => throw new HttpRequestException("offline"));
        var dir = Path.Combine(_tmp, "m2");
        Assert.Null(await new ManifestStore(new HttpClient(offline), dir).GetAsync());

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.yaml"), Fixture);
        var store = new ManifestStore(new HttpClient(offline), dir);
        Assert.NotNull(await store.GetAsync());
    }
}
