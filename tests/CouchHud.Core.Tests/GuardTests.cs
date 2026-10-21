using CouchHud.Core.Guard;
using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

public sealed class GuardTests : IDisposable
{
    private const string Key = @"HKCU\Software\Test\Guard";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-guard-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly List<string> _log = [];

    public GuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string State => Path.Combine(_dir, "guard.json");
    private string VdfFile => Path.Combine(_dir, "config.vdf");

    private GuardSnapshot Create() => new(_reg, new FileVdfStore(), State, _log.Add);

    private static GuardChange Dword(string name, int v) => new(GuardKind.RegistryDword, Key, name, v.ToString());
    private static GuardChange Str(string name, string v) => new(GuardKind.RegistryString, Key, name, v);

    [Fact]
    public void RoundTripRestoresOriginalsAndClearsState()
    {
        _reg.WriteDword(Key, "A", 1);
        _reg.WriteString(Key, "B", "orig");
        var guard = Create();

        Assert.Empty(guard.Apply([Dword("A", 0), Str("B", "new")]));
        Assert.Equal(0, _reg.ReadDword(Key, "A"));
        Assert.Equal("new", _reg.ReadString(Key, "B"));
        Assert.True(File.Exists(State));

        Assert.Empty(guard.Restore());
        Assert.Equal(1, _reg.ReadDword(Key, "A"));
        Assert.Equal("orig", _reg.ReadString(Key, "B"));
        Assert.False(guard.HasPending);
        Assert.False(File.Exists(State));
    }

    [Fact]
    public void MissingStaysMissing()
    {
        var guard = Create();
        guard.Apply([Dword("A", 5), Str("B", "x")]);
        Assert.Equal(5, _reg.ReadDword(Key, "A"));

        guard.Restore();
        Assert.Null(_reg.ReadDword(Key, "A"));
        Assert.Null(_reg.ReadString(Key, "B"));
    }

    [Fact]
    public void DoubleApplyKeepsFirstOriginal()
    {
        _reg.WriteDword(Key, "A", 7);
        var guard = Create();
        guard.Apply([Dword("A", 0)]);
        guard.Apply([Dword("A", 0)]);
        guard.Apply([Dword("A", 2)]);

        Assert.Single(guard.Entries);
        Assert.Equal("7", guard.Entries[0].Original);
        Assert.Equal("2", guard.Entries[0].Applied);
        guard.Restore();
        Assert.Equal(7, _reg.ReadDword(Key, "A"));
    }

    [Fact]
    public void RestoreLeftoversAfterSimulatedCrash()
    {
        _reg.WriteString(Key, "B", "orig");
        File.WriteAllText(VdfFile, "\"Root\"\r\n{\r\n\t\"keep\"\t\t\"1\"\r\n}\r\n");
        var crashed = Create();
        crashed.Apply([Str("B", "new"), Dword("A", 1), new(GuardKind.VdfKey, VdfFile, "Root/x/y", "v"), new(GuardKind.VdfKey, VdfFile, "Root/keep", "2")]);
        // process dies here, a new one starts
        var next = Create();
        Assert.True(next.HasPending);
        // an Apply after the crash must not overwrite the saved original with our own value
        next.Apply([Str("B", "new")]);
        Assert.Empty(next.RestoreLeftovers());

        Assert.Equal("orig", _reg.ReadString(Key, "B"));
        Assert.Null(_reg.ReadDword(Key, "A"));
        Assert.Equal("\"Root\"\r\n{\r\n\t\"keep\"\t\t\"1\"\r\n}\r\n", File.ReadAllText(VdfFile));
        Assert.False(File.Exists(State));
    }

    [Fact]
    public void BrokenVdfFileDoesNotCrashRestore()
    {
        File.WriteAllText(VdfFile, "\"Root\"\n{\n\t\"a\"\t\t\"1\"\n}\n");
        var guard = Create();
        guard.Apply([new(GuardKind.VdfKey, VdfFile, "Root/a", "2"), Str("B", "x")]);
        File.WriteAllText(VdfFile, "\"Root\" { \"a\" ");

        var errors = guard.Restore();
        Assert.Single(errors);
        Assert.Null(_reg.ReadString(Key, "B")); // the healthy entry was still restored
        Assert.Single(guard.Entries);           // the broken one stays for a later retry
        Assert.True(File.Exists(State));
        Assert.NotEmpty(_log);
    }

    [Fact]
    public void CorruptStateFileStartsEmptyWithoutThrowing()
    {
        File.WriteAllText(State, "{ not json");
        var guard = Create();
        Assert.False(guard.HasPending);
        Assert.NotEmpty(_log);
        Assert.Empty(guard.Apply([Dword("A", 1)]));
    }

    [Fact]
    public void VdfKeyRestoredByteExactWhenOriginalExisted()
    {
        const string text = "// c\n\"Root\"\n{\n\t\"a\"\t\t\"1\"\n}\n";
        File.WriteAllText(VdfFile, text);
        var guard = Create();
        guard.Apply([new(GuardKind.VdfKey, VdfFile, "root/A", "9")]);
        Assert.Equal("9", VdfPatch.Get(File.ReadAllText(VdfFile), "Root/a"));
        guard.Restore();
        Assert.Equal(text, File.ReadAllText(VdfFile));
    }
}

public class VdfPatchTests
{
    private const string Lf = "// head\n\"Root\"\n{\n\t\"a\"\t\t\"1\"\n\t\"Blk\"\n\t{\n\t\t\"x\"\t\t\"y\"   // tail\n\t}\n\t\"z\" \"q\\\\w \\\"e\\\"\"\n}\n";
    private static readonly string Crlf = Lf.Replace("\n", "\r\n");

    [Theory]
    [InlineData("Root/a", "1")]
    [InlineData("root/BLK/X", "y")]
    [InlineData("Root/z", "q\\w \"e\"")]
    [InlineData("Root/nope", null)]
    public void Get(string path, string? expected)
    {
        Assert.Equal(expected, VdfPatch.Get(Lf, path));
        Assert.Equal(expected, VdfPatch.Get(Crlf, path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetExistingChangesOnlyTheValue(bool crlf)
    {
        var src = crlf ? Crlf : Lf;
        var result = VdfPatch.Set(src, "root/blk/X", "new");
        Assert.Equal(src.Replace("\"y\"   // tail", "\"new\"   // tail"), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetCreatesKeyInExistingBlock(bool crlf)
    {
        var src = crlf ? Crlf : Lf;
        var nl = crlf ? "\r\n" : "\n";
        var result = VdfPatch.Set(src, "Root/Blk/n", "1");
        Assert.Equal(src.Replace($"// tail{nl}\t}}", $"// tail{nl}\t\t\"n\"\t\t\"1\"{nl}\t}}"), result);
    }

    [Fact]
    public void SetCreatesMissingBlocksWithTabs()
    {
        var result = VdfPatch.Set(Lf, "Root/New/Deep/k", "v");
        var expected = Lf.Replace("\"e\\\"\"\n}", "\"e\\\"\"\n\t\"New\"\n\t{\n\t\t\"Deep\"\n\t\t{\n\t\t\t\"k\"\t\t\"v\"\n\t\t}\n\t}\n}");
        Assert.Equal(expected, result);
        Assert.Equal("v", VdfPatch.Get(result, "root/new/deep/K"));
    }

    [Fact]
    public void SetOnEmptyAndNewTopLevel()
    {
        Assert.Equal("\"A\"\r\n{\r\n\t\"k\"\t\t\"v\"\r\n}\r\n", VdfPatch.Set("", "A/k", "v"));
        var result = VdfPatch.Set("\"A\"\n{\n}", "B/k", "v");
        Assert.Equal("\"A\"\n{\n}\n\"B\"\n{\n\t\"k\"\t\t\"v\"\n}\n", result);
    }

    [Fact]
    public void SetEscapesValue()
    {
        var result = VdfPatch.Set(Lf, "Root/a", "p\\q \"r\"");
        Assert.Contains("\"p\\\\q \\\"r\\\"\"", result);
        Assert.Equal("p\\q \"r\"", VdfPatch.Get(result, "Root/a"));
    }

    [Fact]
    public void SetIntoBlockWithBraceOnSameLine()
    {
        var result = VdfPatch.Set("\"R\" { \"a\" \"1\" }", "R/b", "2");
        Assert.Equal("2", VdfPatch.Get(result, "R/b"));
        Assert.Equal("1", VdfPatch.Get(result, "R/a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteRemovesWholeLineOnly(bool crlf)
    {
        var src = crlf ? Crlf : Lf;
        var nl = crlf ? "\r\n" : "\n";
        Assert.Equal(src.Replace($"\t\"a\"\t\t\"1\"{nl}", ""), VdfPatch.Delete(src, "Root/a"));
        Assert.Equal(src.Replace("\"x\"\t\t\"y\"", ""), VdfPatch.Delete(src, "Root/Blk/x"));
    }

    [Fact]
    public void DeleteBlockAndMissing()
    {
        var result = VdfPatch.Delete(Lf, "Root/blk");
        Assert.Equal(Lf.Replace("\t\"Blk\"\n\t{\n\t\t\"x\"\t\t\"y\"   // tail\n\t}\n", ""), result);
        Assert.Equal(Lf, VdfPatch.Delete(Lf, "Root/none/x"));
    }

    [Fact]
    public void SetThenDeleteRestoresOriginalText()
    {
        var added = VdfPatch.Set(Crlf, "Root/Blk/n", "1");
        Assert.Equal(Crlf, VdfPatch.Delete(added, "Root/Blk/n", pruneFrom: 2));
        var deep = VdfPatch.Set(Lf, "Root/Aa/B/c", "1");
        Assert.Equal(Lf, VdfPatch.Delete(deep, "Root/Aa/B/c", pruneFrom: 1));
    }

    [Fact]
    public void BrokenInputThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => VdfPatch.Set("\"R\" { \"a\" ", "R/a", "1"));
    }
}
