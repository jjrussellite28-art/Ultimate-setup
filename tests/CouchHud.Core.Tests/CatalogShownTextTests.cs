using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// The catalog rows must show the description in the UI language. The signed statement of a package carries one English text; the official
/// index carries it per language, so the index wins there. A source the user added is shown from its signed statement.
/// </summary>
public sealed class CatalogShownTextTests
{
    private static CatalogEntry Entry(string description, bool extra) =>
        new("weather", "Weather", description, "kewe87", CatalogKind.Plugin, "1.0.0", null, null, null, null, null, "CouchHud.Plugin.Weather", "https://example.org/index.json", null)
        { Extra = extra, LongDescription = description + " (long)" };

    [Fact]
    public void Official_entry_shows_the_index_text_not_the_english_statement()
    {
        var entry = Entry("Das Wetter im Kopf", extra: false);
        Assert.Equal("Das Wetter im Kopf", CatalogRules.ShownSummary(entry, "The weather in the header"));
        Assert.Equal("Das Wetter im Kopf (long)", CatalogRules.ShownLongDescription(entry, "The weather, long"));
    }

    [Fact]
    public void Statement_is_used_when_the_index_has_no_text()
    {
        var entry = Entry("", extra: false) with { LongDescription = null };
        Assert.Equal("The weather in the header", CatalogRules.ShownSummary(entry, "The weather in the header"));
        Assert.Equal("The weather, long", CatalogRules.ShownLongDescription(entry, "The weather, long"));
    }

    [Fact]
    public void Added_source_is_shown_from_its_signed_statement()
    {
        var entry = Entry("Unsigned index text", extra: true);
        Assert.Equal("Signed statement", CatalogRules.ShownSummary(entry, "Signed statement"));
        Assert.Equal("Signed long", CatalogRules.ShownLongDescription(entry, "Signed long"));
    }
}
