using System.IO.Pipes;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Named pipes are network code for the lint: a pipe name may carry another PC's name and then goes out over SMB. A plugin that talks to a
/// program on this PC through a pipe declares network:loopback; without any network capability the
/// package is refused. Also the name of the FanControl plugin is CouchHUD's own.
/// </summary>
public sealed class PluginPipeLintTests
{
    private static readonly IReadOnlyList<LintFinding> Findings = PluginLint.Scan(File.ReadAllBytes(typeof(PluginPipeLintTests).Assembly.Location), "Tests.dll");

    [Theory]
    [InlineData("System.IO.Pipes.NamedPipeClientStream")]
    [InlineData("System.IO.Pipes.NamedPipeServerStream")]
    [InlineData("System.IO.Pipes.AnonymousPipeServerStream")]
    public void A_pipe_is_network_code_and_any_network_capability_covers_it(string detail)
    {
        var pipe = Findings.Where(f => f.Detail == detail).ToList();
        Assert.NotEmpty(pipe);
        // Besides the network finding a third party that declares services also gets "own network connection" (86a8871): both are fine.
        Assert.Contains(pipe, f => f.Capability == "network");
        foreach (var origin in new[] { PluginOrigin.FirstParty, PluginOrigin.ThirdParty })
        {
            Assert.NotEmpty(PluginLint.Evaluate(pipe, origin, [PluginCapabilities.Pages, PluginCapabilities.Process, PluginCapabilities.Native]));
            Assert.Empty(PluginLint.Evaluate(pipe, origin, [PluginCapabilities.NetworkLoopback]));
            Assert.Empty(PluginLint.Evaluate(pipe, origin, [PluginCapabilities.NetworkLan]));
            Assert.Empty(PluginLint.Evaluate(pipe, origin, [PluginCapabilities.NetworkInternet]));
        }
    }

    [Theory]
    [InlineData("fancontrol")]
    [InlineData("FanControl")]
    [InlineData("fan-control")]
    [InlineData("fancontrol_plus")]
    [InlineData("FanContro1")]
    public void The_name_of_the_FanControl_plugin_is_reserved(string name) => Assert.True(PluginNames.IsReserved(name));

    [Fact]
    public void Other_fan_names_stay_free() => Assert.False(PluginNames.IsReserved("fanspeed"));
}

/// <summary>Never called: gives the lint something to find in this assembly's metadata.</summary>
internal static class PipeBait
{
#pragma warning disable CA1416
    internal static object[] Uses() =>
    [
        new NamedPipeClientStream(".", "nothing", PipeDirection.InOut),
        new NamedPipeServerStream("nothing"),
        new AnonymousPipeServerStream()
    ];
#pragma warning restore CA1416
}
