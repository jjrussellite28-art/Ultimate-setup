using CouchHud.Core.Guard;
using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

/// <summary>"Paste from PC" for tokens, keys and addresses in the settings.</summary>
public class ClipboardTextTests
{
    [Theory]
    [InlineData("  eyJhbGciOiJIUzI1NiJ9.token  ", "eyJhbGciOiJIUzI1NiJ9.token")]
    [InlineData("http://homeassistant.local:8123\r\n", "http://homeassistant.local:8123")]
    [InlineData("sk-ant-abc def", "sk-ant-abc def")]
    public void Text_is_trimmed(string raw, string expected) => Assert.Equal(expected, ClipboardText.Clean(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n ")]
    [InlineData("first line\nsecond line")]
    [InlineData("tab\tinside")]
    public void Nothing_or_control_characters_inside_give_nothing(string? raw) => Assert.Null(ClipboardText.Clean(raw));

    [Fact]
    public void At_most_4096_characters()
    {
        Assert.NotNull(ClipboardText.Clean(new string('a', ClipboardText.MaxLength)));
        Assert.Null(ClipboardText.Clean(new string('a', ClipboardText.MaxLength + 1)));
    }

    [Fact]
    public void The_clipboard_and_the_code_chain_are_closed_while_the_couch_is_locked()
    {
        Assert.DoesNotContain("readClipboard", LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("setParentCode", LockPolicy.OpenWhileLocked);
    }
}
