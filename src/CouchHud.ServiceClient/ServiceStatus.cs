using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient;

/// <summary>The state of the CouchHUD service as the UI shows it.</summary>
public enum ServiceState
{
    /// <summary>Not looked at yet.</summary>
    Unknown,

    /// <summary>Runs, answers, is ours and not older than the shell.</summary>
    Running,

    /// <summary>The copy of CouchHUD has no service by design (not installed into a protected folder: a dev build or an unpacked copy).</summary>
    NotExpected,

    NotInstalled,
    Stopped,
    Disabled,

    /// <summary>Start pending (boot): it will answer soon.</summary>
    Starting,

    /// <summary>The SCM says it runs, but it does not answer (in time).</summary>
    NotResponding,

    /// <summary>Someone else holds the pipe name, or the service stopped because of that. Alarm.</summary>
    Squatted,

    /// <summary>The service answered against the protocol. Alarm.</summary>
    Inconsistent,

    /// <summary>It answers, but it is older than the shell (setup did not replace it) or speaks another protocol.</summary>
    OldVersion,

    /// <summary>It answers, but refuses to act: its folder is not protected.</summary>
    UnsafeInstall
}

/// <summary>One look at the service. <see cref="Version"/> is what Hello said (null when it did not answer).</summary>
public sealed record ServiceStatus(ServiceState State, string? Version, DateTimeOffset At)
{
    public static readonly ServiceStatus Unknown = new(ServiceState.Unknown, null, DateTimeOffset.MinValue);

    public ServiceHealth Health => FailClosed.Classify(State);

    /// <summary>The state as the UI names it (camelCase of the enum).</summary>
    public string Name => State switch
    {
        ServiceState.NotExpected => "notExpected",
        ServiceState.NotInstalled => "notInstalled",
        ServiceState.NotResponding => "notResponding",
        ServiceState.OldVersion => "oldVersion",
        ServiceState.UnsafeInstall => "unsafeInstall",
        _ => State.ToString().ToLowerInvariant()
    };
}

/// <summary>Finds out the <see cref="ServiceStatus"/>: the SCM first, then a Hello over the checked pipe.</summary>
public sealed class ServiceProbe(IServiceControl scm, ServiceClient client, Version appVersion, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private bool _lastWasRetry;

    /// <summary>
    /// <paramref name="startIfStopped"/>: a stopped (not disabled, not squatted) service is started first (interactive users may start it).
    /// <paramref name="startWait"/>: how long a starting service is waited for (at boot the shell can be quicker than the service).
    /// </summary>
    public async Task<ServiceStatus> ProbeAsync(bool startIfStopped, TimeSpan startWait, Func<DateTimeOffset> now, CancellationToken cancellation = default)
    {
        var info = scm.Query();
        if (info.State == ScmState.Stopped && !info.StoppedBySquatting && !info.Disabled && startIfStopped && scm.TryStart())
        {
            info = scm.Query();
        }
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(250);
        while (info.State == ScmState.StartPending && waited < startWait)
        {
            await _delay(step, cancellation).ConfigureAwait(false);
            waited += step;
            info = scm.Query();
        }
        if (FromScm(info) is { } early) return new ServiceStatus(early, null, now());

        var reply = await client.CallAsync(Operation.Hello, cancellation: cancellation).ConfigureAwait(false);
        var state = FromHello(reply, appVersion, out var version, _lastWasRetry);
        _lastWasRetry = reply.Outcome == CallOutcome.Refused && ErrorMap.ClassifyCode(reply.Code) == FailureKind.Retry;
        return new ServiceStatus(state, version, now());
    }

    /// <summary>The state from the SCM alone; null when it runs and Hello has to tell the rest.</summary>
    public static ServiceState? FromScm(ScmInfo info) => info.State switch
    {
        ScmState.Running when info.ProcessId > 0 => null,
        ScmState.NotInstalled => ServiceState.NotInstalled,
        ScmState.Stopped when info.StoppedBySquatting => ServiceState.Squatted,
        ScmState.Stopped when info.Disabled => ServiceState.Disabled,
        ScmState.Stopped => ServiceState.Stopped,
        ScmState.StartPending => ServiceState.Starting,
        _ => ServiceState.NotResponding
    };

    /// <summary>
    /// The state from the answer to Hello. "Busy" or "rate limited" is the service answering in protocol, so it counts as running, but
    /// only for one look: <paramref name="retryAlreadyCounted"/> (the look before was such an answer too) makes it "not responding"
    /// (review M1: a service that keeps saying "busy" must not pass as healthy for ever).
    /// </summary>
    public static ServiceState FromHello(ServiceReply reply, Version appVersion, out string? version, bool retryAlreadyCounted = false)
    {
        version = null;
        switch (reply.Outcome)
        {
            case CallOutcome.Ok:
                var hello = reply.DataAs(ProtocolJsonContext.Default.HelloData);
                if (hello is null || string.IsNullOrEmpty(hello.ServiceVersion)) return ServiceState.Inconsistent;
                version = hello.ServiceVersion.Length <= 32 ? hello.ServiceVersion : hello.ServiceVersion[..32];
                if (hello.Protocol != Protocol.Version) return ServiceState.OldVersion;
                if (hello.UnsafeInstall) return ServiceState.UnsafeInstall;
                return IsOlder(hello.ServiceVersion, appVersion) ? ServiceState.OldVersion : ServiceState.Running;
            case CallOutcome.Refused:
                return ErrorMap.ClassifyCode(reply.Code) switch
                {
                    FailureKind.NotSupported => ServiceState.OldVersion,
                    FailureKind.UnsafeInstall => ServiceState.UnsafeInstall,
                    // Busy or rate limited: it is the service and it answered in protocol, once.
                    FailureKind.Retry => retryAlreadyCounted ? ServiceState.NotResponding : ServiceState.Running,
                    _ => ServiceState.NotResponding
                };
            case CallOutcome.Squatted:
                return ServiceState.Squatted;
            case CallOutcome.Inconsistent:
                return ServiceState.Inconsistent;
            case CallOutcome.NotInstalled:
                return ServiceState.NotInstalled;
            case CallOutcome.NotRunning:
                return ServiceState.Stopped;
            default:
                return ServiceState.NotResponding;
        }
    }

    /// <summary>The service is a newer release than the shell (an update it ran has replaced it): major.minor.patch above the shell's.</summary>
    public static bool IsNewer(string? serviceVersion, Version appVersion)
    {
        if (!Version.TryParse(serviceVersion, out var service)) return false;
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
        return Norm(service) > Norm(appVersion);
    }

    /// <summary>Major.minor.patch of the service below the shell's: the setup did not replace the service. Unreadable counts as older.</summary>
    public static bool IsOlder(string serviceVersion, Version appVersion)
    {
        if (!Version.TryParse(serviceVersion, out var service)) return true;
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
        return Norm(service) < Norm(appVersion);
    }
}

/// <summary>The keys of the texts that name a service state (Strings: "service.state.&lt;name&gt;").</summary>
public static class ServiceTexts
{
    public static string StateKey(ServiceState state) => "service.state." + new ServiceStatus(state, null, default).Name;

    public static IEnumerable<string> StateKeys => Enum.GetValues<ServiceState>().Select(StateKey);
}

/// <summary>Healthy, merely not there, or evidence that someone is in the way.</summary>
public enum ServiceHealth
{
    Healthy,

    /// <summary>Not usable (missing, stopped, not answering, old, unsafe): privileged steps do not happen, nothing opens.</summary>
    Unavailable,

    /// <summary>Squatted or inconsistent: the same, and Windows is locked at once (manipulation suspected).</summary>
    Alarm,

    /// <summary>No service by design (unprotected copy): nothing privileged is possible, nothing is expected.</summary>
    NotExpected
}
