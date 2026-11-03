using System.Reflection;
using System.Text.RegularExpressions;
using CouchHud.Core.Library;
using CouchHud.Core.Plugins;
using CouchHud.Core.Saves;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

/// <summary>In .NET a trailing "$" also matches before a final line feed, so "abc\n" would pass an exact-match allow-list. These use "\z".</summary>
public class ExactMatchRegexTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // A string literal that starts with "^" and ends with "$": an exact-match pattern that still uses "$".
    private static readonly Regex DollarAtTheEnd = new(@"@?""\^(?:[^""\\]|\\.|"""")*[^\\]\$""");

    private static Regex Field(Type type, string name) =>
        (Regex)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static Regex Generated(Type type, string name) =>
        (Regex)type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

    private static void Exact(Regex pattern, string valid)
    {
        Assert.Matches(pattern, valid);
        Assert.DoesNotMatch(pattern, valid + "\n");
    }

    [Fact]
    public void Package_statement_patterns_refuse_a_trailing_line_feed()
    {
        Exact(Field(typeof(PackageStatements), "HashPattern"), Hash);
        Exact(Field(typeof(PackageStatements), "IdPattern"), "my-plugin");
        Exact(Field(typeof(PackageStatements), "FolderPattern"), "MyPlugin");
    }

    [Fact]
    public void Catalog_patterns_refuse_a_trailing_line_feed()
    {
        Exact(Field(typeof(CatalogParser), "IdPattern"), "my-plugin");
        Exact(Field(typeof(CatalogParser), "HashPattern"), Hash);
        Exact(Field(typeof(SemVer), "Pattern"), "1.2.3");
        var file = Path.GetTempFileName();
        try { Assert.Throws<CatalogException>(() => CatalogRules.VerifySha256(file, Hash + "\n")); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Setup_checksum_line_refuses_a_trailing_line_feed()
    {
        Exact(Field(typeof(ReleaseIntegrity), "Line"), Hash + "  CouchHud-Setup-1.0.0.exe");
    }

    [Fact]
    public void Artwork_cache_names_refuse_a_trailing_line_feed()
    {
        Exact(Generated(typeof(ArtworkCache), "HashPattern"), Hash);
        Exact(Generated(typeof(ArtworkCache), "FilePattern"), Hash + ".png");
    }

    [Fact]
    public void Save_backup_ids_refuse_a_trailing_line_feed()
    {
        Assert.True(SaveBackupService.IsValidId("backup 1.zip"));
        Assert.False(SaveBackupService.IsValidId("backup 1.zip\n"));
    }

    [Fact]
    public void Exact_match_allow_lists_in_the_source_end_with_backslash_z()
    {
        // WingetProgress parses single lines of console output (not an allow-list), so it is left alone.
        var found = new List<string>();
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || Path.GetFileName(file) == "WingetProgress.cs") continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (DollarAtTheEnd.IsMatch(lines[i])) found.Add($"{Path.GetRelativePath(src, file)}:{i + 1}");
        }
        Assert.True(found.Count == 0, "End the exact-match pattern with \\z instead of $: " + string.Join(", ", found));
    }

    [Fact]
    public void The_source_guard_finds_a_dollar_at_the_end()
    {
        Assert.Matches(DollarAtTheEnd, "new(@\"^[a-z]{1,4}$\", RegexOptions.Compiled)");
        Assert.DoesNotMatch(DollarAtTheEnd, "new(@\"^[a-z]{1,4}\\z\", RegexOptions.Compiled)");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
