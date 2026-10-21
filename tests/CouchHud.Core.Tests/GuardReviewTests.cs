using System.Globalization;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

/// <summary>Regression tests for the guard review findings.</summary>
public sealed class GuardReviewTests : IDisposable
{
    private const string Key = @"HKCU\Software\Test\Guard";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-guardr-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly List<string> _log = [];

    public GuardReviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string State => Path.Combine(_dir, "guard.json");
    private string VdfFile => Path.Combine(_dir, "config.vdf");

    private GuardSnapshot Create(IVdfStore? vdf = null) => new(_reg, vdf ?? new FileVdfStore(), State, _log.Add);

    private static GuardChange Vdf(string file, string path, string value) => new(GuardKind.VdfKey, file, path, value);

    private sealed class ThrowingVdf : IVdfStore
    {
        private readonly FileVdfStore _inner = new();
        public bool Exists(string file) => _inner.Exists(file);
        public VdfLookup Lookup(string file, string keyPath) => _inner.Lookup(file, keyPath);
        public void Write(string file, string keyPath, string value) => throw new IOException("disk full");
        public void WriteRaw(string file, string keyPath, string rawToken) => _inner.WriteRaw(file, keyPath, rawToken);
        public void Delete(string file, string keyPath, int? pruneFrom = null) => _inner.Delete(file, keyPath, pruneFrom);
        public void DeleteFileIfBlank(string file) => _inner.DeleteFileIfBlank(file);
    }

    // 1
    [Fact]
    public void BlockTargetIsNotTouchedAndNeverDeleted()
    {
        const string text = "\"Root\"\n{\n\t\"Blk\"\n\t{\n\t\t\"x\"\t\t\"y\"\n\t}\n}\n";
        File.WriteAllText(VdfFile, text);
        var guard = Create();
        Assert.Single(guard.Apply([Vdf(VdfFile, "Root/Blk", "v")]));
        Assert.False(guard.HasPending);
        Assert.Empty(guard.Restore());
        Assert.Equal(text, File.ReadAllText(VdfFile));
    }

    [Fact]
    public void FailedWriteDropsTheNewEntry()
    {
        File.WriteAllText(VdfFile, "\"Root\"\n{\n}\n");
        var guard = Create(new ThrowingVdf());
        Assert.Single(guard.Apply([Vdf(VdfFile, "Root/k", "v")]));
        Assert.False(guard.HasPending);
        Assert.False(File.Exists(State));
    }

    // 2
    [Fact]
    public void RegistryTypeMismatchIsNotTouched()
    {
        _reg.WriteString(Key, "A", "text");
        _reg.WriteDword(Key, "B", 3);
        var guard = Create();
        var errors = guard.Apply([new(GuardKind.RegistryDword, Key, "A", "0"), new(GuardKind.RegistryString, Key, "B", "x")]);
        Assert.Equal(2, errors.Count);
        Assert.False(guard.HasPending);
        Assert.Equal("text", _reg.ReadString(Key, "A"));
        Assert.Equal(3, _reg.ReadDword(Key, "B"));
        Assert.Empty(guard.Restore());
        Assert.Equal("text", _reg.ReadString(Key, "A"));
        Assert.Equal(3, _reg.ReadDword(Key, "B"));
    }

    // 3 + 10
    [Fact]
    public void DwordsUseInvariantCultureAndFullRange()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            var big = unchecked((int)0xFFFFFFF0);
            _reg.WriteDword(Key, "Big", big);
            _reg.WriteDword(Key, "Neg", -7);
            var guard = Create();
            Assert.Empty(guard.Apply([
                new(GuardKind.RegistryDword, Key, "Big", "4294967295"),
                new(GuardKind.RegistryDword, Key, "Neg", "-5")]));
            Assert.Equal(-1, _reg.ReadDword(Key, "Big"));
            Assert.Equal(-5, _reg.ReadDword(Key, "Neg"));
            Assert.DoesNotContain('−', File.ReadAllText(State));

            Assert.Empty(guard.Restore());
            Assert.Equal(big, _reg.ReadDword(Key, "Big"));
            Assert.Equal(-7, _reg.ReadDword(Key, "Neg"));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    // 4
    [Fact]
    public void InvalidUtf8FileIsLeftUntouched()
    {
        byte[] bytes = [.. "\"Root\"\n{\n\t\"k\"\t\t\"caf"u8.ToArray(), 0xE9, .. "\"\n}\n"u8.ToArray()];
        File.WriteAllBytes(VdfFile, bytes);
        var guard = Create();
        Assert.Single(guard.Apply([Vdf(VdfFile, "Root/n", "v")]));
        Assert.Equal(bytes, File.ReadAllBytes(VdfFile));
        Assert.False(guard.HasPending);
    }

    // 5
    [Fact]
    public void PreexistingEmptyBlocksSurviveRestore()
    {
        const string text = "\"Root\"\n{\n\t\"Empty\"\n\t{\n\t}\n}\n";
        File.WriteAllText(VdfFile, text);
        var guard = Create();
        Assert.Empty(guard.Apply([Vdf(VdfFile, "Root/Empty/k", "v"), Vdf(VdfFile, "Root/New/k", "v")]));
        Assert.Equal("v", VdfPatch.Get(File.ReadAllText(VdfFile), "Root/Empty/k"));
        Assert.Empty(guard.Restore());
        Assert.Equal(text, File.ReadAllText(VdfFile));
    }

    // 6
    [Fact]
    public void PlainValueAsParentIsRejected()
    {
        const string text = "\"Root\"\n{\n\t\"a\"\t\t\"1\"\n}\n";
        Assert.Throws<InvalidOperationException>(() => VdfPatch.Set(text, "Root/a/x", "1"));
        File.WriteAllText(VdfFile, text);
        var guard = Create();
        Assert.Single(guard.Apply([Vdf(VdfFile, "Root/a/x", "1")]));
        Assert.False(guard.HasPending);
        Assert.Equal(text, File.ReadAllText(VdfFile));
    }

    // 7
    [Fact]
    public void UnknownEscapesComeBackByteExact()
    {
        const string text = "\"Root\"\n{\n\t\"k\"\t\t\"a\\rb\\x\"\n}\n";
        File.WriteAllText(VdfFile, text);
        var guard = Create();
        Assert.Empty(guard.Apply([Vdf(VdfFile, "Root/k", "new")]));
        Assert.Empty(guard.Restore());
        Assert.Equal(text, File.ReadAllText(VdfFile));
    }

    // 8
    [Fact]
    public void CreatedFileIsDeletedOnRestore()
    {
        var file = Path.Combine(_dir, "sub", "new.vdf");
        var guard = Create();
        Assert.Empty(guard.Apply([Vdf(file, "A/B/k", "v"), Vdf(file, "A/B/k2", "v")]));
        Assert.True(File.Exists(file));
        Assert.Empty(guard.Restore());
        Assert.False(File.Exists(file));
    }

    // 9
    [Fact]
    public void InlineBlockRoundTripsByteExact()
    {
        const string text = "\"Root\" { \"x\" \"1\" }\n";
        var set = VdfPatch.Set(text, "Root/y", "2");
        Assert.Equal("2", VdfPatch.Get(set, "Root/y"));
        Assert.Equal(text, VdfPatch.Delete(set, "Root/y"));

        var deep = VdfPatch.Set(text, "Root/N/k", "2");
        Assert.Equal("2", VdfPatch.Get(deep, "Root/N/k"));
        Assert.Equal(text, VdfPatch.Delete(deep, "Root/N/k", pruneFrom: 1));
    }

    // 11
    [Fact]
    public void SaveFailureIsLoggedAndNothingIsApplied()
    {
        Directory.CreateDirectory(State); // a directory where the state file should go
        _reg.WriteDword(Key, "A", 1);
        var guard = Create();
        var errors = guard.Apply([new(GuardKind.RegistryDword, Key, "A", "0")]);
        Assert.NotEmpty(errors);
        Assert.Contains(_log, l => l.Contains("not saved"));
        Assert.Equal(1, _reg.ReadDword(Key, "A"));
        Assert.False(guard.HasPending);
    }

    [Fact]
    public void CorruptStateFallsBackToBackup()
    {
        _reg.WriteDword(Key, "A", 1);
        _reg.WriteDword(Key, "B", 2);
        var guard = Create();
        guard.Apply([new(GuardKind.RegistryDword, Key, "A", "0")]);
        guard.Apply([new(GuardKind.RegistryDword, Key, "B", "0")]);
        File.WriteAllText(State, "{ broken");

        var next = Create();
        Assert.True(next.HasPending);
        Assert.Empty(next.RestoreLeftovers());
        Assert.Equal(1, _reg.ReadDword(Key, "A")); // the backup knew A
    }

    [Fact]
    public void LockHeldByAnotherProcessMakesApplyFailCleanly()
    {
        _reg.WriteDword(Key, "A", 1);
        var guard = Create();
        guard.LockTimeout = TimeSpan.FromMilliseconds(150);
        using var held = new FileStream(State + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Single(guard.Apply([new(GuardKind.RegistryDword, Key, "A", "0")]));
        Assert.Equal(1, _reg.ReadDword(Key, "A"));
    }

    [Fact]
    public void RestoreKeepsEntriesInMemoryWhenSaveFails()
    {
        if (!OperatingSystem.IsWindows()) return; // relies on read-only files blocking delete
        _reg.WriteDword(Key, "A", 1);
        var guard = Create();
        guard.Apply([new(GuardKind.RegistryDword, Key, "A", "0")]);
        File.SetAttributes(State, FileAttributes.ReadOnly);

        var errors = guard.Restore();
        Assert.NotEmpty(errors);
        Assert.True(guard.HasPending); // memory still matches the file
        Assert.Equal(1, _reg.ReadDword(Key, "A"));
    }
}
