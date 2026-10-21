using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.Logging;
using CouchHud.Service.Pipe;

namespace CouchHud.Service.Tests;

internal sealed class FakeClock : IMonotonicClock
{
    public TimeSpan Now { get; set; } = TimeSpan.FromHours(1);

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class CapturingSink : IEventSink
{
    public List<string> Lines { get; } = [];

    public void Info(string message)
    {
        lock (Lines) Lines.Add("I " + message);
    }

    public void Error(string message)
    {
        lock (Lines) Lines.Add("E " + message);
    }

    public string All
    {
        get { lock (Lines) return string.Join("\n", Lines); }
    }
}

/// <summary>One valid grant of one kind (default: the couch code, the weakest).</summary>
internal sealed class FakeGrants(string valid, GrantKinds kind = GrantKinds.Couch) : IGrantStore
{
    public int Consumed { get; private set; }

    public GrantKinds LastAccepted { get; private set; }

    public bool TryConsume(CallerInfo caller, string grant, Operation operation, GrantKinds accepted, out GrantKinds used)
    {
        LastAccepted = accepted;
        used = GrantKinds.None;
        if (grant != valid || (accepted & kind) == 0) return false;
        Consumed++;
        used = kind;
        return true;
    }
}

internal sealed class FixedCodeState(bool enrolled) : ICodeState
{
    public bool IsEnrolled(string sid) => enrolled;
}

/// <summary>Reads from one stream and writes to another: a client's frames in, the service's answers out.</summary>
internal sealed class DuplexStream(Stream input, Stream output) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
}

/// <summary>A client that connects and then sends nothing (or only some bytes): reads wait until cancelled.</summary>
internal sealed class StallingStream(byte[] prefix) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public MemoryStream Written { get; } = new();
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < prefix.Length)
        {
            buffer.Span[0] = prefix[_position++];
            return 1;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Written.WriteAsync(buffer, cancellationToken);
}

internal static class Callers
{
    public const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    /// <summary>A standard user at the console: medium IL, interactive, Administrators not live.</summary>
    public static CallerInfo User(string sid = UserSid) => new(
        Sid: sid, LogonId: 0x1234567, ProcessId: 4242, PipeSessionId: 1, TokenSessionId: 1, IntegrityLevel: IntegrityLevels.Medium,
        IsInteractive: true, IsNetwork: false, IsService: false, IsAnonymous: false, IsAppContainer: false, IsRestricted: false,
        IsAdminGroupEnabled: false);

    /// <summary>An administrator after "run as administrator": the group is live, high IL.</summary>
    public static CallerInfo ElevatedAdmin() => User() with { IntegrityLevel = IntegrityLevels.High, IsAdminGroupEnabled = true };
}

/// <summary>A dispatcher and a connection handler wired like the service, with fakes.</summary>
internal sealed class Harness
{
    public FakeClock Clock { get; }
    public CapturingSink Log { get; } = new();
    public RecentEvents Events { get; }
    public RateLimiter Limiter { get; }
    public RunningWork Work { get; }
    public StateChecks Checks { get; }
    public Dispatcher Dispatcher { get; }
    public ConnectionHandler Connections { get; }
    public ConnectionQuota Quota { get; }
    public DenyLog DenyLog { get; }
    public bool UnsafeInstall { get; set; }
    public ServiceState State { get; }

    /// <param name="onServiceList">The service's own list of pausable services (the denylist applies on top, as in the service).</param>
    public Harness(IGrantStore? grants = null, bool codeEnrolled = true, ConnectionLimits? limits = null,
        Action<Dictionary<Operation, Handler>>? extraHandlers = null, Action<StateChecks>? extraChecks = null,
        Func<string, bool>? onServiceList = null, TimeSpan? handlerTimeout = null, ICodeState? codeState = null, FakeClock? clock = null,
        Func<string, LockView>? lockViews = null)
    {
        Clock = clock ?? new FakeClock();
        Events = new RecentEvents(Clock, () => 1000);
        Limiter = new RateLimiter(Clock);
        Work = new RunningWork(4, 16);
        DenyLog = new DenyLog(Log, Clock);
        var state = State = new ServiceState("0.4.1", () => UnsafeInstall, () => 7);
        var handlers = BasicHandlers.Create(state, Events, () => 123);
        extraHandlers?.Invoke(handlers);
        var onList = onServiceList ?? (n => n == "GamingServices");
        var codes = codeState ?? new FixedCodeState(codeEnrolled);
        Checks = StateChecks.BuiltIn(onList, codes);
        extraChecks?.Invoke(Checks);
        Dispatcher = new Dispatcher(new DispatcherParts(
            new RequestValidator(n => ServicePolicy.IsAllowed(n, onList)), handlers, Checks, Limiter, grants ?? NoGrants.Instance,
            codes, Work, DenyLog, Events, () => UnsafeInstall, handlerTimeout ?? TimeSpan.FromSeconds(2), lockViews));
        Quota = new ConnectionQuota(4);
        Connections = new ConnectionHandler(Dispatcher, Quota, DenyLog, limits ?? new ConnectionLimits(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 8));
    }

    public static readonly IStateCheck AllowAll = new StateCheckFunc((_, _) => AuthorizationDecision.Allowed);

    public static Handler Ok => (_, _) => ValueTask.FromResult(ProtocolJson.Serialize(Response.Success()));

    /// <summary>Sends the frames over one connection and returns the answers.</summary>
    public async Task<List<byte[]>> ExchangeAsync(CallerInfo? caller, params byte[][] frames)
    {
        var input = new MemoryStream(frames.SelectMany(f => f).ToArray());
        var output = new MemoryStream();
        await Connections.HandleAsync(new DuplexStream(input, output), caller, CancellationToken.None);
        return ReadFrames(output.ToArray());
    }

    public static List<byte[]> ReadFrames(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        var answers = new List<byte[]>();
        while (FrameCodec.ReadAsync(stream).AsTask().GetAwaiter().GetResult() is { } frame) answers.Add(frame);
        return answers;
    }

    public async Task<Response> OneAsync(CallerInfo? caller, byte[] json)
    {
        var answers = await ExchangeAsync(caller, FrameCodec.Encode(json));
        return ProtocolJson.DeserializeResponse(Strip(Assert.Single(answers)))!;
    }

    /// <summary>The data part is not in <see cref="Response"/>: drop it to read only {v, ok, code}.</summary>
    public static byte[] Strip(byte[] json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("data");
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }
}