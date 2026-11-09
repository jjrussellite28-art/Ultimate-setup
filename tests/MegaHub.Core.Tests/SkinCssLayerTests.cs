using System.Text.Json;
using MegaHub.Core.Skins;
using Xunit;

namespace MegaHub.Core.Tests;

/// <summary>
/// A skin cannot use <c>@layer</c> (a layer declared first beats the important rules of everything after it, so a skin could take the core's pins apart)
/// or <c>@import</c> (a second stylesheet nobody checked, with <c>layer()</c> and <c>supports()</c>). The statements go, a layer block is unwrapped into
/// ordinary rules, the text is read the way CSS tokenizes it (an unquoted url( runs to its closing bracket, a string ends at CR, LF and FF), and a backstop
/// refuses the whole stylesheet when an at-keyword "@layer" or "@import" (escapes resolved) is still a token anywhere in it; the same words in a comment, a string, a url() or a name are no at-keyword. The same cases are read by scripts/ui-logic-check.mjs
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
    [InlineData("@lay@import \"x\";er x;a{b:c}")]
    [InlineData("@imp@import \"x\";ort \"y\";a{b:c}")]
    [InlineData("@\\6c @import \"x\";ayer x;a{b:c}")]
    [InlineData("@l\u001bayer ch{}")]
    public void A_stylesheet_in_which_the_removal_joins_an_at_keyword_is_refused_whole(string css)
    {
        Assert.Equal("", SkinCss.Sanitize(css, out var refused));
        Assert.True(refused);
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
    [InlineData(":root{--note:\"@layer\";--bg:url(@import.png)}")]
    [InlineData("a[data-x=\"@layer\"],b[title='@import']{c:d}")]
    [InlineData(".a\\@layer,.sm\\@import,#\\@LAYER{b:c}")]
    [InlineData("@/**/layer x;a{b:c}")]
    public void The_words_in_a_comment_string_url_or_name_are_no_at_keyword_and_are_served_as_they_are(string css)
    {
        Assert.Equal(css, SkinCss.Sanitize(css, out var refused));
        Assert.False(refused);
    }

    [Theory]
    [InlineData("/* @layer */@layer ch{a::before{content:\"@import\"}}b{c:d}", "/* @layer */a::before{content:\"@import\"}b{c:d}")]
    [InlineData("@/**/layer x;@layer/**/ch;a{b:c}", "@/**/layer x;a{b:c}")]
    [InlineData("@\\6c\r\nayer ch;a{b:c}", "a{b:c}")]
    [InlineData("@\\6c\fayer ch;a{b:c}", "a{b:c}")]
    [InlineData("@\\6c\rayer ch;a{b:c}", "a{b:c}")]
    [InlineData("@\\4C\\41YER ch;a{b:c}", "a{b:c}")]
    [InlineData("a{content:\"x\r}@layer ch{c{d:e}}", "a{content:\"x\r}c{d:e}")]
    public void Real_at_rules_next_to_those_words_are_still_removed(string css, string expected)
    {
        Assert.Equal(expected, SkinCss.Sanitize(css, out var refused));
        Assert.False(refused);
    }

    [Theory]
    [InlineData("a{background:url(/\\/evil.com/x.png)}")]
    [InlineData("a{background:url(\\2f/e.com)}")]
    [InlineData("a{background:url(\\2f \\2f e.com)}")]
    [InlineData("a{background:url(https\\3a\\2f\\2fe.com/x)}")]
    [InlineData("a{background:url(\"/\\/evil.com/x.png\")}")]
    [InlineData("@font-face{font-family:x;src:url(\\2f/e.com/f.woff2)}")]
    [InlineData("a{background-image:image-set(\"/\\/e.com/x.png\" 1x)}")]
    [InlineData("a{background:url(\"/\\\n/evil.com\")}")]
    public void An_address_spelled_with_escapes_in_a_url_or_a_fetching_string_is_refused(string css)
    {
        Assert.Equal("", SkinCss.Sanitize(css, out var refused));
        Assert.True(refused);
    }

    [Theory]
    [InlineData("a{background:url(\"/\t/evil.com/x\")}")]
    [InlineData("a{background:url(\"/\\9 /evil.com/x\")}")]
    [InlineData("a{background:url(\"/\\a /evil.com/x\")}")]
    [InlineData("a{background:url(\"/\\d /evil.com/x\")}")]
    [InlineData("a{background:url(/\\9 /evil.com)}")]
    [InlineData("a{background-image:image-set(\"/\\9 /evil.com/x\" 1x)}")]
    [InlineData("a{--u:\"\\2f \\2f evil.com/x\";background-image:image-set(var(--u) 1x)}")]
    [InlineData("a{--u:\"http:evil.com/x\";background-image:image-set(var(--u) 1x)}")]
    [InlineData("a{background:url(http:evil.com/x)}")]
    [InlineData("a{background:url(\"http:/evil.com\")}")]
    [InlineData("a{background:url(\"\\\\\\\\evil.com\\\\x\")}")]
    [InlineData("a{background:url(\"https://local.megahub:x@evil.com/y\")}")]
    public void The_address_is_judged_as_the_url_parser_reads_it_tabs_line_breaks_backslashes_schemes_and_custom_properties(string css)
    {
        Assert.Equal("", SkinCss.Sanitize(css, out var refused));
        Assert.True(refused);
    }

    [Theory]
    [InlineData("a{background:url(\"ht\uFEFFtp:evil.com/x\")}")]
    [InlineData("a{background:url(\"https\u200B:evil.com/x\")}")]
    [InlineData("a{background:attr(data-x url)}")]
    [InlineData("a{background-image:image-set(attr(data-x type(<string>)) 1x)}")]
    [InlineData("a::before{content:attr(title)}")]
    [InlineData("a{background:ATTR(data-x url)}")]
    [InlineData("a{background:at\\74r(data-x url)}")]
    [InlineData("a{--u:\"foo:bar\";background-image:image-set(var(--u) 1x)}")]
    [InlineData(":root{--skin:\"user:nova\"}")]
    [InlineData("a{b:c}:root{--x:\"x:y\"}")]
    [InlineData(":root{/* c */--b:\"x:y\"}")]
    public void Attr_and_schemes_in_custom_properties_or_split_by_ignored_characters_are_refused(string css)
    {
        Assert.Equal("", SkinCss.Sanitize(css, out var refused));
        Assert.True(refused);
    }

    [Theory]
    [InlineData("//\uFEFFevil.com/x")]
    [InlineData("//\u200Bevil.com/x")]
    [InlineData("//\u00ADevil.com/x")]
    [InlineData("//\u0085evil.com/x")]
    [InlineData("//\u00A0evil.com/x")]
    [InlineData("//\u3000evil.com/x")]
    public void An_address_after_slashes_with_an_ignored_or_unicode_space_character_is_replaced_not_served(string address)
    {
        var served = SkinCss.Sanitize("a{background:url(\"" + address + "\")}", out var refused);
        Assert.False(refused);
        Assert.DoesNotContain("evil.com", served);
    }

    [Theory]
    [InlineData("a::before{content:\"// \"}b::before{content:\"a: b\"}c::before{content:\"Note:\"}")]
    [InlineData("html[data-skin=\"user:nova\"]{font-family:\"Segoe UI\",sans-serif}@font-face{font-family:\"X Y\";src:url(\"fonts/x.woff2\") format(\"woff2\")}")]
    [InlineData("a{background:url(\"https://local.megahub/a.png\")}b{background:url(//art.megahub/b.png)}")]
    [InlineData("a{background:url(\"data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27/%3E\")}b{background:url(about:blank)}")]
    public void Decorative_strings_attribute_values_fonts_and_the_apps_own_hosts_are_served(string css)
    {
        Assert.Equal(css, SkinCss.Sanitize(css, out var refused));
        Assert.False(refused);
    }

    [Fact]
    public void The_catalog_skins_and_the_ui_stylesheet_are_served_unchanged_when_they_are_at_hand()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ui", "styles.css"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var files = new List<string> { Path.Combine(dir!.FullName, "ui", "styles.css") };
        // The catalog repository lies next to the app's folder (or, in a worktree under .claude/worktrees, three levels up next to it).
        foreach (var catalog in new[] { Path.Combine(dir.FullName, "..", "couchhud-catalog-src", "skins"), Path.Combine(dir.FullName, "..", "..", "..", "..", "couchhud-catalog-src", "skins") })
        {
            if (Directory.Exists(catalog)) files.AddRange(Directory.GetFiles(catalog, "*.css", SearchOption.AllDirectories));
        }
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.Equal(text, SkinCss.Sanitize(text, out var refused));
            Assert.False(refused, file);
        }
    }

    [Theory]
    [InlineData("a{background:url(img/x.png)}b{background:url(\"/art/y.png\")}c{background:url(data:image/png;base64,AAAA)}")]
    [InlineData("a::before{content:\"\\2f\\2f\"}")]
    [InlineData("a{background:url(img\\20x.png)}b{background:url(\"i\\41.png\")}")]
    public void Plain_paths_and_escapes_that_spell_no_address_are_served(string css)
    {
        Assert.Equal(css, SkinCss.Sanitize(css, out var refused));
        Assert.False(refused);
    }

    [Theory]
    [InlineData("a{content:\"//x\\\"} @layer base;\n")]
    [InlineData("a{content:\"//x\\\"}} @import \"evil.css\";\n")]
    [InlineData("a{}\n//x/*\n{}\n@layer base;\n*/\n")]
    [InlineData("a{content:'//x\\'} @layer base;\n")]
    [InlineData("a{b:url(//x\\)} @import \"evil.css\";\n")]
    [InlineData("/* //x*/ @layer base;\n/* */a{b:c}")]
    [InlineData("a{b:c}@//x@layer base;\n")]
    public void The_replacement_of_web_addresses_never_turns_string_or_comment_text_into_a_rule(string css)
    {
        // Whatever is served has been scanned itself: no at-keyword in it and nothing left for the remover to change.
        var served = SkinCss.Sanitize(css, out _);
        Assert.False(SkinCss.MentionsLayerOrImport(served));
        Assert.Equal(served, SkinCss.StripLayersAndImports(served));
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
