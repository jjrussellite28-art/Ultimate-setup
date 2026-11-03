namespace CouchHud.Core.Tests;

/// <summary>Source pin: the pause branch of the service switch runs through the admin service's grant wrapper.</summary>
public sealed class ServiceSwitchPauseSourceTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Pause_runs_through_WithGrantAsync_and_resume_does_not_need_it()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Services", "ServiceSwitch.cs")).Replace("\r\n", "\n");
        Assert.Contains("AdminService.WithGrantAsync(CouchHud.Service.Contracts.Operation.PauseServices, () => ops.PauseServicesAsync(names))", text);
        // No call of PauseServicesAsync outside that wrapper.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "PauseServicesAsync\\(").Count);
        Assert.Contains("ops.ResumeServicesAsync(names)", text);
    }
}
