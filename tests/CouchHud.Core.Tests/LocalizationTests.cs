using System.Globalization;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Tests;

public partial class LocalizationTests
{
    [GeneratedRegex(@"\{(\d+)")]
    private static partial Regex Placeholder();

    private static string[] Indices(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void German_and_english_have_exactly_the_same_keys()
    {
        var de = Strings.German.Keys.ToHashSet();
        var en = Strings.English.Keys.ToHashSet();
        Assert.Empty(de.Except(en));
        Assert.Empty(en.Except(de));
        Assert.NotEmpty(de);
    }

    [Fact]
    public void Every_key_has_the_same_placeholders_in_both_languages()
    {
        foreach (var (key, de) in Strings.German)
        {
            Assert.True(Indices(de).SequenceEqual(Indices(Strings.English[key])), $"placeholders differ for {key}");
        }
    }

    [Fact]
    public void No_text_is_empty()
    {
        foreach (var table in new[] { Strings.German, Strings.English })
        {
            foreach (var (key, text) in table) Assert.False(string.IsNullOrWhiteSpace(text), $"{key} is empty");
        }
    }

    [Fact]
    public void Plural_keys_come_in_one_other_pairs()
    {
        foreach (var table in new[] { Strings.German, Strings.English })
        {
            foreach (var key in table.Keys)
            {
                if (key.EndsWith(".one", StringComparison.Ordinal)) Assert.True(table.ContainsKey(key[..^4] + ".other"), $"{key} has no .other");
                if (key.EndsWith(".other", StringComparison.Ordinal)) Assert.True(table.ContainsKey(key[..^6] + ".one"), $"{key} has no .one");
            }
        }
    }

    [Theory]
    [InlineData("auto", "de-DE", "de")]
    [InlineData("auto", "de-AT", "de")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "fr-FR", "en")]
    [InlineData(null, "fr-FR", "en")]
    [InlineData("de", "en-US", "de")]
    [InlineData("en", "de-DE", "en")]
    [InlineData("EN", "de-DE", "en")]
    public void Resolve_maps_the_setting_and_the_windows_language(string? setting, string culture, string expected)
    {
        Assert.Equal(expected, Strings.Resolve(setting, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Unknown_key_falls_back_to_the_key_itself()
    {
        Assert.Equal("no.such.key", Strings.GetIn("de", "no.such.key"));
        Assert.Equal("no.such.key", Strings.GetIn("en", "no.such.key"));
    }

    [Fact]
    public void Get_formats_arguments_per_language()
    {
        Assert.Equal("Ordner nicht gefunden: C:\\x", Strings.GetIn("de", "files.folderNotFound", "C:\\x"));
        Assert.Equal("Folder not found: C:\\x", Strings.GetIn("en", "files.folderNotFound", "C:\\x"));
    }

    [Fact]
    public void Plural_picks_one_or_other()
    {
        Assert.Equal("1 Minute", Strings.PluralIn("de", "parental.minutes", 1));
        Assert.Equal("5 Minuten", Strings.PluralIn("de", "parental.minutes", 5));
        Assert.Equal("1 minute", Strings.PluralIn("en", "parental.minutes", 1));
        Assert.Equal("5 minutes", Strings.PluralIn("en", "parental.minutes", 5));
        Assert.Equal("0 minutes", Strings.PluralIn("en", "parental.minutes", 0));
    }
}
