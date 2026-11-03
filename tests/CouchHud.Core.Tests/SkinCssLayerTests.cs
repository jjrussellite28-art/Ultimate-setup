using System.Text.Json;
using CouchHud.Core.Skins;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>
/// A skin cannot use <c>@layer</c> (a layer declared first beats the important rules of everything after it, so a skin could take the core's pins apart)
/// or <c>@import</c> (a second stylesheet nobody checked, with <c>layer()</c> and <c>supports()</c>). The statements go, a layer block is unwrapped into
/// ordinary rules, the text is read the way CSS tokenizes it (an unquoted url( runs to its closing bracket, a string ends at CR, LF and FF), and a backstop
/// refuses the whole stylesheet when "@layer" or "@import" is still anywhere in it, escapes resolved. The same cases are read by scripts/ui-logic-check.mjs
/// (scripts/skin-css.js) and fed to headless Edge to see that no such rule is left; tests/skin-css-cases.json is the shared list.
/// </summary>
public class SkinCssLayerTests
{
    private sealed record Case(string Name, string Input, string Output);

    private static List<Case> LoadCases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "skin-css-cases.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "tests", "skin-css-cases.json")));
        return doc.RootElement.EnumerateArray()
            .Select(e => new Case(e.GetProperty("name").GetString()!, e.GetProperty("input").GetString()!, e.GetProperty("output").GetString()!))
            .ToList();
    }

    public static IEnumerable<object[]> SharedCases() => LoadCases().Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void The_shared_cases_give_the_same_output_as_the_script_version(string name)
    {
        var c = LoadCases().Single(x => x.Name == name);
        Assert.Equal(c.Output, SkinCss.Sanitize(c.Input));
    }

    [Fact]
    public void The_shared_list_has_the_hand_written_cases_and_the_fuzz_corpus()
    {
        var cases = LoadCases();
        Assert.True(cases.Count >= 450, $"{cases.Count} cases");
        Assert.Contains(cases, c => c.Name == "imp-unk-urlc");
        Assert.Contains(cases, c => c.Name == "layer-string-cr");
        Assert.Equal(cases.Count, cases.Select(c => c.Name).Distinct().Count());
    }

    [Theory]
    [InlineData("@import url(x.css) layer(ch);a{b:c}", "a{b:c}")]
    [InlineData("@layer ch;a{b:c}", "a{b:c}")]
    [InlineData("@layer ch{a{b:c}}", "a{b:c}")]
    [InlineData("a{b:url(/*)}@layer ch{c{d:e}}/* */", "a{b:url(/*)}c{d:e}/* */")]
    [InlineData("@x url(/*);@import \"e.css\" layer(ch);/* */", "@x url(/*);/* */")]
    [InlineData("a{content:\"\r}@layer ch{x{y:z}}\"}", "a{content:\"\r}x{y:z}\"}")]
    public void What_the_tokenizer_finds_is_removed(string css, string expected)
    {
        Assert.Equal(expected, SkinCss.Sanitize(css, out var refused));
        Assert.False(refused);
    }

    [Theory]
    [InlineData("a{content:\"@layer x;\"}")]
    [InlineData("a{content:'@import url(x)'}")]
    [InlineData("/* @layer x; */a{b:c}")]
    [InlineData("a{b:url(@layer)}")]
    [InlineData("a{content:\"\\40 LAYER\"}")]
    [InlineData("/* @\\6c ayer */a{b:c}")]
    [InlineData("a{content:\"x\\\n}@layer ch{c{d:e}}\"}")]
    [InlineData("a{b:c}@layerx{d:e}")]
    public void A_stylesheet_that_still_mentions_layer_or_import_is_refused_whole(string css)
    {
        Assert.Equal("", SkinCss.Sanitize(css, out var refused));
        Assert.True(refused);
    }

    [Theory]
    [InlineData("a{b:c}")]
    [InlineData("@media screen{a{b:c}}@font-face{font-family:x;src:url(x.woff2)}")]
    [InlineData("a{b:url(\"x.png\")}c{d:url( 'y.png' )}e{f:url(  g.png  )}")]
    [InlineData(":root { --accent: #ff2bd6; }")]
    public void An_honest_stylesheet_is_served_as_it_is(string css)
    {
        Assert.Equal(css, SkinCss.Sanitize(css, out var refused));
        Assert.False(refused);
    }

    [Fact]
    public void An_unfinished_layer_or_import_does_not_throw()
    {
        Assert.Equal("a{b:c}", SkinCss.Sanitize("a{b:c}@import url(x"));
        Assert.Equal("a{b:c}", SkinCss.Sanitize("a{b:c}@layer x"));
        Assert.Equal("a{b:c}", SkinCss.Sanitize("a{b:c}@layer x{"));
        Assert.Equal("}", SkinCss.Sanitize("@layer x{}}"));
    }

    [Fact]
    public void A_utf16_stylesheet_or_bytes_that_are_not_utf8_are_refused()
    {
        // The host reads the file as UTF-8 and sends charset=utf-8; a byte order mark of UTF-16 would still make the browser read UTF-16 (a mark beats the header).
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("@layer ch{a{b:c}}")).ToArray();
        Assert.Equal("", SkinCss.Sanitize(utf16, out var refused));
        Assert.True(refused);
        var utf16be = new byte[] { 0xFE, 0xFF }.Concat(System.Text.Encoding.BigEndianUnicode.GetBytes("a{b:c}")).ToArray();
        Assert.Equal("", SkinCss.Sanitize(utf16be, out refused));
        Assert.True(refused);
        // ISO-2022-JP bytes with an escape sequence, Shift_JIS or Latin-1 bytes above 0x7F that are no valid UTF-8.
        Assert.Equal("", SkinCss.Sanitize(new byte[] { 0x61, 0x7B, 0x62, 0x3A, 0xFF, 0x7D }, out refused));
        Assert.True(refused);
        Assert.Equal("", SkinCss.Sanitize(System.Text.Encoding.ASCII.GetBytes("@charset \"iso-2022-jp\";\n@\u001b(Blayer ch{x{y:z}}"), out refused));
        Assert.True(refused);
    }

    [Fact]
    public void A_utf8_stylesheet_is_served_with_or_without_its_byte_order_mark()
    {
        var text = "a{content:\"Grüße 日本\"}";
        var plain = new System.Text.UTF8Encoding(false).GetBytes(text);
        var marked = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(plain).ToArray();
        Assert.Equal(text, SkinCss.Sanitize(plain, out var refused));
        Assert.False(refused);
        Assert.Equal(text, SkinCss.Sanitize(marked, out refused));
        Assert.False(refused);
    }

    [Theory]
    [InlineData("@charset \"utf-8\";a{b:c}", "a{b:c}")]
    [InlineData("@charset \"UTF-8\" ;a{b:c}", "a{b:c}")]
    public void A_charset_of_utf8_is_dropped_the_host_forces_it_anyway(string css, string expected)
    {
        Assert.Equal(expected, SkinCss.Sanitize(css, out var refused));
        Assert.False(refused);
    }
}
