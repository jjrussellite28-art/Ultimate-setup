using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>Catalog categories: the index names an id, the core names it in German and English; anything unknown is "other", an old index still works.</summary>
public sealed class CatalogCategoryTests
{
    private static string Index(params string[] entries) =>
        "{ \"version\": 1, \"entries\": [" + string.Join(",", entries) + "] }";

    private static string Entry(string id, string type = "plugin", string? category = null, bool omitCategory = false) =>
        "{ \"id\": \"" + id + "\", \"type\": \"" + type + "\", \"name\": \"" + id + "\", \"version\": \"1.0.0\", \"download\": \"https://example.com/" + id + ".zip\", \"sha256\": \""
        + new string('a', 64) + "\"" + (omitCategory ? "" : category is null ? ", \"category\": null" : ", \"category\": \"" + category + "\"") + " }";

    private static CatalogEntry One(string json) => CatalogParser.Parse(Index(json), CatalogDefaults.IndexUrl).Single();

    [Theory]
    [InlineData("smartHome", "smartHome")]
    [InlineData("Smart-Home", "smartHome")]
    [InlineData("smart_home", "smartHome")]
    [InlineData("STREAMING", "streaming")]
    [InlineData("hardware", "hardware")]
    [InlineData("media", "media")]
    [InlineData("ai", "ai")]
    [InlineData("information", "information")]
    [InlineData("hud", "hud")]
    [InlineData("skins", "skins")]
    [InlineData("other", "other")]
    public void A_known_category_is_taken_as_the_core_names_it(string raw, string expected) =>
        Assert.Equal(expected, CatalogCategories.Normalize(raw, CatalogKind.Plugin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("games")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("smartHome; DROP TABLE")]
    public void An_unknown_or_missing_category_falls_back_to_other_for_a_plugin_and_to_skins_for_a_skin(string? raw)
    {
        Assert.Equal(CatalogCategories.Other, CatalogCategories.Normalize(raw, CatalogKind.Plugin));
        Assert.Equal(CatalogCategories.Skins, CatalogCategories.Normalize(raw, CatalogKind.Skin));
    }

    [Theory]
    [InlineData("smartHome")]
    [InlineData("streaming")]
    [InlineData("hardware")]
    [InlineData("media")]
    [InlineData("ai")]
    [InlineData("information")]
    [InlineData("other")]
    public void A_skin_is_only_skins_or_hud(string raw) =>
        Assert.Equal(CatalogCategories.Skins, CatalogCategories.Normalize(raw, CatalogKind.Skin));

    [Fact]
    public void A_very_long_value_is_not_looked_at()
    {
        Assert.Equal(CatalogCategories.Other, CatalogCategories.Normalize(new string('a', 5000), CatalogKind.Plugin));
    }

    [Fact]
    public void The_index_names_the_category_per_entry_and_an_old_index_without_it_keeps_working()
    {
        var entries = CatalogParser.Parse(Index(Entry("lights", category: "smartHome"), Entry("old", omitCategory: true), Entry("odd", category: "wat"), Entry("theme", type: "skin", omitCategory: true), Entry("hudskin", type: "skin", category: "hud")),
            CatalogDefaults.IndexUrl);

        Assert.Equal("smartHome", entries.Single(e => e.Id == "lights").Category);
        Assert.Equal("other", entries.Single(e => e.Id == "old").Category);
        Assert.Equal("other", entries.Single(e => e.Id == "odd").Category);
        Assert.Equal("skins", entries.Single(e => e.Id == "theme").Category);
        Assert.Equal("hud", entries.Single(e => e.Id == "hudskin").Category);
        // Every entry is still installable: the category changes nothing about the checks.
        Assert.All(entries, e => Assert.Null(e.Problem));
    }

    [Fact]
    public void A_category_that_is_not_a_string_is_ignored()
    {
        var json = "{ \"id\": \"x\", \"type\": \"plugin\", \"name\": \"x\", \"version\": \"1.0.0\", \"category\": {\"de\": \"Spiele\"}, \"download\": \"https://example.com/x.zip\", \"sha256\": \"" + new string('b', 64) + "\" }";
        Assert.Equal("other", One(json).Category);
    }

    [Fact]
    public void Every_category_has_a_name_in_both_languages_from_the_core_and_never_the_raw_text()
    {
        foreach (var language in new[] { "de", "en" })
        {
            var names = CatalogCategories.All.Select(c => CatalogCategories.LabelIn(language, c)).ToList();
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n) || n.StartsWith("catalog.category", StringComparison.Ordinal)));
            Assert.Equal(names.Count, names.Distinct().Count());
        }
        Assert.Equal("Sonstige", CatalogCategories.LabelIn("de", "other"));
        Assert.Equal("Medien", CatalogCategories.LabelIn("de", "media"));
        Assert.Equal("Other", CatalogCategories.LabelIn("en", "other"));
        Assert.Equal("Media", CatalogCategories.LabelIn("en", "media"));
        // Free text from an index never becomes a name.
        Assert.Equal("Other", CatalogCategories.LabelIn("en", "<b>boom</b>"));
    }

    [Fact]
    public void The_list_is_in_a_fixed_order_with_other_last_and_the_ids_are_unique()
    {
        Assert.Equal("other", CatalogCategories.All[^1]);
        Assert.Equal(CatalogCategories.All.Count, CatalogCategories.All.Distinct().Count());
        Assert.Contains("hud", CatalogCategories.All);
        Assert.Contains("skins", CatalogCategories.All);
    }
}
