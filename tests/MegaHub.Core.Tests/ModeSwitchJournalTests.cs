using MegaHub.Core.Config;
using Xunit;

namespace MegaHub.Core.Tests;

public class ModeSwitchJournalTests
{
    [Theory]
    [InlineData("")]
    [InlineData("""{"from":"app","steps":[]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"lock","before":{"m":"off","c":true}},{"id":"shell","before":null}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"autostart","before":{"r":true,"e":[["steam","pc"]]}},{"id":"services","before":{"k":false,"s":["WSearch"]}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"release","before":{"p":true}},{"id":"services","before":{"k":true,"s":["WSearch"]}}]}""")]
    // The applied list next to the selection (0.6.4): a pending selection stays pending after an undo.
    [InlineData("""{"from":"app","steps":[{"id":"services","before":{"k":false,"s":["WSearch"],"i":[]}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"services","before":{"k":true,"s":["WSearch","SysMain"],"i":["WSearch"]}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"suppress","before":{"v":false,"b":false}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"suppress","before":{"v":true,"b":true}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"light","before":{"m":"full","s":true,"l":"code"}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"release"},{"id":"profiles","before":{"p":["a"]}},{"id":"light","before":{"m":"full","s":true}}]}""")]
    public void The_switch_record_shape_is_accepted(string text) => Assert.True(ModeSwitchJournal.IsValid(text));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"from":"pc","steps":[]}""")]
    [InlineData("""{"from":"app"}""")]
    [InlineData("""{"from":"app","steps":[],"extra":1}""")]
    [InlineData("""{"from":"app","steps":[{"id":"Lock!"}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"lock","before":"text"}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"lock","cmd":"x"}]}""")]
    // Forged values: an id that is not a step, a mode outside the known set, keys of another step, nested or wrongly typed values.
    [InlineData("""{"from":"app","steps":[{"id":"evil","before":null}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"lock","before":{"m":"admin"}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"lock","before":{"s":true}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"lock","before":{"m":{"x":1}}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"lock"},{"id":"lock"}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"shell","before":{"s":"yes"}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"services","before":{"s":["a",1]}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"services","before":{"i":"WSearch"}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"suppress","before":{"v":true,"b":"yes"}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"services","before":{"i":[""]}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"autostart","before":{"e":[["id","off"]]}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"release","before":{"m":"off"}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"release","before":{"p":"yes"}}]}""")]
    [InlineData("""{"from":"app","steps":[{"id":"integration","before":{"m":"custom"}}]}""")]
    [InlineData("""{"from":"shell","steps":[{"id":"light","before":{"l":"admin"}}]}""")]
    public void Anything_else_is_refused(string text) => Assert.False(ModeSwitchJournal.IsValid(text));

    [Fact]
    public void An_oversized_or_overlong_record_is_refused()
    {
        Assert.False(ModeSwitchJournal.IsValid(new string(' ', ModeSwitchJournal.MaxLength + 1)));
        var steps = string.Join(",", Enumerable.Repeat("""{"id":"lock"}""", 17));
        Assert.False(ModeSwitchJournal.IsValid("""{"from":"app","steps":[""" + steps + "]}"));
        // An oversized name or list inside "before".
        var longName = new string('a', 97);
        Assert.False(ModeSwitchJournal.IsValid("{\"from\":\"app\",\"steps\":[{\"id\":\"profiles\",\"before\":{\"p\":[\"" + longName + "\"]}}]}"));
        var many = string.Join(",", Enumerable.Repeat("\"a\"", 25));
        Assert.False(ModeSwitchJournal.IsValid("""{"from":"app","steps":[{"id":"profiles","before":{"p":[""" + many + "]}}]}"));
    }
}
