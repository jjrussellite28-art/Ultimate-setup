using System.Diagnostics;
using System.ServiceProcess;
using CouchHud.Privileged;

namespace CouchHud.Privileged.Tests;

/// <summary>
/// The uninstall's "service" step: disabled before stopped (no SCM restart, no user start in between), killed only when it hangs,
/// then deleted; native programs from the system folder only. And the ProgramData state: links are never followed.
/// </summary>
public class ServiceRemovalTests
{
    private const string System32 = @"C:\Windows\System32";

    private sealed class Launcher(Func<string, string[], int>? exit = null) : IProcessLauncher
    {
        public List<string> Calls { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add(Path.GetFileName(fileName) + " " + string.Join(' ', arguments));
            Assert.Equal(System32, Path.GetDirectoryName(fileName));
            return new ProcessResult(exit?.Invoke(Path.GetFileName(fileName), arguments.ToArray()) ?? 0, "", "");
        }

        public int? StartDetached(string fileName, IReadOnlyList<string> arguments) => throw new InvalidOperationException();
    }

    private sealed class Events : IEventSink
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add(message);

        public void Error(string message) => Lines.Add("! " + message);
    }

    private static ServiceRemoval Removal(Launcher launcher, Func<ServiceControllerStatus?> status) =>
        new(launcher, new Events(), status, _ => { }, System32);

    [Fact]
    public void A_service_that_is_not_installed_is_left_alone()
    {
        var launcher = new Launcher();
        Assert.True(Removal(launcher, () => null).Remove());
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void A_running_service_is_disabled_then_stopped_then_deleted()
    {
        var launcher = new Launcher();
        var state = ServiceControllerStatus.Running;
        var removal = new ServiceRemoval(new StopOnStop(launcher, () => state = ServiceControllerStatus.Stopped), new Events(), () => state, _ => { }, System32);
        Assert.True(removal.Remove());
        Assert.Equal(["sc.exe config CouchHud start= disabled", "sc.exe stop CouchHud", "sc.exe delete CouchHud"], launcher.Calls);
    }

    [Fact]
    public void A_hanging_service_is_ended_by_its_service_name_and_deleted_anyway()
    {
        var launcher = new Launcher();
        Assert.True(Removal(launcher, () => ServiceControllerStatus.StopPending).Remove());
        Assert.Equal("sc.exe config CouchHud start= disabled", launcher.Calls[0]);
        Assert.Contains("taskkill.exe /F /FI SERVICES eq CouchHud", launcher.Calls);
        Assert.Equal("sc.exe delete CouchHud", launcher.Calls[^1]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(ServiceInstall.ErrorServiceDoesNotExist, true)]
    [InlineData(ServiceInstall.ErrorServiceMarkedForDelete, true)]
    [InlineData(5, false)]
    public void The_result_follows_sc_delete(int code, bool gone)
    {
        var launcher = new Launcher((_, args) => args[0] == "delete" ? code : 0);
        Assert.Equal(gone, Removal(launcher, () => ServiceControllerStatus.Stopped).Remove());
        Assert.DoesNotContain(launcher.Calls, c => c.StartsWith("sc.exe stop", StringComparison.Ordinal));
    }

    [Fact]
    public void An_exception_does_not_fail_the_uninstall()
    {
        Assert.False(Removal(new Launcher(), () => throw new UnauthorizedAccessException()).Remove());
    }

    /// <summary>Stops the fake service when "sc stop" runs.</summary>
    private sealed class StopOnStop(Launcher inner, Action stop) : IProcessLauncher
    {
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            if (arguments.Count > 0 && arguments[0] == "stop") stop();
            return inner.Run(fileName, arguments, timeout);
        }

        public int? StartDetached(string fileName, IReadOnlyList<string> arguments) => throw new InvalidOperationException();
    }

    [Fact]
    public void Unknown_service_state_counts_as_failure_not_as_not_installed()
    {
        // Access denied (or any SCM error but "does not exist") must not lead to the data cleanup.
        var launcher = new Launcher();
        var removal = Removal(launcher, () => throw new InvalidOperationException("denied", new System.ComponentModel.Win32Exception(5)));
        Assert.False(removal.Remove());
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void The_data_folder_is_inside_the_service_folder()
    {
        Assert.Equal(@"C:\Program Files\CouchHud\service\data", ServiceInstall.DataFolder(@"C:\Program Files\CouchHud\service"));
        Assert.Equal(@"C:\Program Files\CouchHud\service\data\logs", ServiceInstall.SetupLogFolder(@"C:\Program Files\CouchHud\service"));
    }
}
