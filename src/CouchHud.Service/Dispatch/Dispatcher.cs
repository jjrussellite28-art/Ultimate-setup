using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Logging;

namespace CouchHud.Service.Dispatch;

/// <summary>What a handler gets: the request that passed the validator and the authorization, and the caller from the token. The account an operation acts on is always <c>Caller.Sid</c>.</summary>
public sealed record RequestContext(CallerInfo Caller, ValidatedRequest Request)
{
    /// <summary>The kind of grant that opened this request (None: no grant was needed or none counted; an elevated administrator needs none).</summary>
    public GrantKinds Grant { get; init; }

    private StreamReceiver? _receiver;

    /// <summary>
    /// A handler that answers <see cref="ErrorCodes.StreamReady"/> hands over what reads the bytes that follow. The connection runs it
    /// after the answer went out; if the answer never goes out (timeout, failure, a dispatch without a stream), it is disposed instead.
    /// </summary>
    public void AcceptStream(StreamReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (Interlocked.CompareExchange(ref _receiver, receiver, null) is not null) throw new InvalidOperationException("a stream was accepted already");
    }

    /// <summary>Takes the receiver out (once): whoever gets it runs or disposes it.</summary>
    internal StreamReceiver? TakeReceiver() => Interlocked.Exchange(ref _receiver, null);
}

/// <summary>
/// Reads the bytes after a "streamReady" answer and gives the final answer. <see cref="ReceiveAsync"/> must not throw for anything the
/// client does (a broken stream is an answer with an error code); <see cref="Dispose"/> gives back whatever the receiver holds when
/// it is not run or after it ran.
/// </summary>
public abstract class StreamReceiver : IDisposable
{
    public abstract Task<byte[]> ReceiveAsync(Stream pipe, CancellationToken cancellation);

    public abstract void Dispose();
}

/// <summary>The answer of a dispatch and, after a "streamReady" answer, what reads the stream.</summary>
public sealed record DispatchResult(byte[] Response, StreamReceiver? Receiver);

/// <summary>Carries out one operation and returns the serialized response (see <see cref="ProtocolJson"/>).</summary>
public delegate ValueTask<byte[]> Handler(RequestContext context, CancellationToken cancellation);

/// <summary>The parts the dispatcher decides with.</summary>
public sealed record DispatcherParts(
    RequestValidator Validator,
    IReadOnlyDictionary<Operation, Handler> Handlers,
    StateChecks Checks,
    RateLimiter Limiter,
    IGrantStore Grants,
    ICodeState Codes,
    RunningWork Work,
    DenyLog DenyLog,
    RecentEvents Events,
    Func<bool> UnsafeInstall,
    TimeSpan HandlerTimeout,
    Func<string, LockView>? LockViews = null);

/// <summary>
/// One message in, one answer out: validator (never throws) → install check → authorization (table and state checks) → handler
/// lookup → running-work quota → rate limit → grant → handler with a hard time limit. Every refusal is a <see cref="Response"/>
/// with an error code; the reason goes only to the (throttled) log. Tests drive it with a fake <see cref="CallerInfo"/>.
/// </summary>
public sealed class Dispatcher(DispatcherParts parts)
{
    /// <summary>Operations that still answer when the install location is not protected.</summary>
    private static readonly HashSet<Operation> AllowedWhenUnsafe = [Operation.Hello, Operation.Status, Operation.Ping];

    public IEnumerable<Operation> Implemented => parts.Handlers.Keys;

    /// <summary>One message, one answer; an operation that wants a stream cannot get one on this path (its receiver is disposed).</summary>
    public async ValueTask<byte[]> DispatchAsync(CallerInfo? caller, ReadOnlyMemory<byte> message, CancellationToken cancellation)
    {
        var result = await DispatchWithStreamAsync(caller, message, cancellation).ConfigureAwait(false);
        result.Receiver?.Dispose();
        return result.Response;
    }

    /// <summary>Like <see cref="DispatchAsync"/>, but hands out the receiver of an operation that answered "streamReady" (the caller runs or disposes it).</summary>
    public async ValueTask<DispatchResult> DispatchWithStreamAsync(CallerInfo? caller, ReadOnlyMemory<byte> message, CancellationToken cancellation)
    {
        RequestContext? context = null;
        var response = await DispatchCoreAsync(caller, message, c => context = c, cancellation).ConfigureAwait(false);
        return new DispatchResult(response, context?.TakeReceiver());
    }

    private async ValueTask<byte[]> DispatchCoreAsync(CallerInfo? caller, ReadOnlyMemory<byte> message, Action<RequestContext> started, CancellationToken cancellation)
    {
        // Checked by the connection already; checked again so the dispatcher never runs for an unknown caller.
        if (Authorizer.CheckCaller(caller) is { } callerProblem)
        {
            parts.DenyLog.Error(caller?.Sid, $"[deny] caller refused: {callerProblem}; {caller?.Describe() ?? "no identity"}");
            return Fail(ErrorCodes.Denied);
        }
        var who = caller!;

        var validation = parts.Validator.Validate(message.Span);
        if (!validation.Ok)
        {
            // The detail is printable ASCII and short (ValidationResult.Fail); it never contains argument values the validator accepted.
            parts.DenyLog.Error(who.Sid, $"[reject] {validation.Code}: {validation.Detail}; {who.Describe()}");
            parts.Events.Add(who.Sid, "rejected", $"request refused: {validation.Code}");
            return Fail(validation.Code);
        }
        var request = validation.Request!;
        var op = request.Operation;

        if (parts.UnsafeInstall() && !AllowedWhenUnsafe.Contains(op)) return Refuse(who, op, ErrorCodes.UnsafeInstall, "install location not protected");

        var decision = Authorizer.Authorize(who, request, CodeEnrolled(who.Sid), parts.Checks);
        if (decision.Verdict == Verdict.Deny) return Refuse(who, op, ErrorCodes.Denied, decision.Reason ?? "denied");

        // Before the rate limit and the grant: an operation this version cannot do costs neither an attempt nor a grant.
        if (!parts.Handlers.TryGetValue(op, out var handler)) return Fail(ErrorCodes.NotImplemented);

        // Held until the handler task really ends (see RunAsync), also past a timeout. Health (Hello, Status, Ping) needs no slot:
        // those handlers only read the service's own state, and the UI tells "busy" from "hung" by them.
        var lease = AllowedWhenUnsafe.Contains(op) ? NoLease.Instance : parts.Work.TryStart(who.Sid);
        if (lease is null) return Refuse(who, op, ErrorCodes.Busy, "too much running work");

        var rate = parts.Limiter.TryAcquire(who.Sid, op);
        if (rate.ErrorCode is { } rateCode)
        {
            lease.Dispose();
            return Refuse(who, op, rateCode, $"retry after {rate.RetryAfter.TotalSeconds:0} s");
        }

        var used = GrantKinds.None;
        if (decision.Verdict == Verdict.NeedGrant && (request.Grant is null || !parts.Grants.TryConsume(who, request.Grant, op, AcceptedGrants(who, request), out used)))
        {
            lease.Dispose();
            return Refuse(who, op, ErrorCodes.NeedGrant, request.Grant is null ? "none given" : "not valid");
        }

        var context = new RequestContext(who, request) { Grant = used };
        started(context);
        return await RunAsync(handler, context, lease, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the handler on the thread pool and waits at most the handler timeout: the answer then is "timeout" even if the
    /// handler ignores its token. The lease ends when the handler task really ends, not when the caller stops waiting.
    /// </summary>
    private async Task<byte[]> RunAsync(Handler handler, RequestContext context, IDisposable lease, CancellationToken cancellation)
    {
        var op = context.Request.Operation;
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var abandoned = 0;
        var task = Task.Run(() => handler(context, limit.Token).AsTask(), CancellationToken.None);
        _ = task.ContinueWith(done =>
        {
            _ = done.Exception;   // observed: an abandoned handler's failure is no unobserved-task event
            // A handler that accepted a stream after its caller stopped waiting: nobody will run the receiver.
            if (Volatile.Read(ref abandoned) == 1) context.TakeReceiver()?.Dispose();
            lease.Dispose();
            limit.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        byte[] Abandon(string code)
        {
            Volatile.Write(ref abandoned, 1);
            context.TakeReceiver()?.Dispose();
            return Fail(code);
        }

        try
        {
            return await task.WaitAsync(parts.HandlerTimeout, cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryCancel(limit);
            parts.DenyLog.Error(context.Caller.Sid, $"[timeout] {op}; {context.Caller.Describe()}");
            return Abandon(ErrorCodes.Timeout);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            parts.DenyLog.Error(context.Caller.Sid, $"[timeout] {op}; {context.Caller.Describe()}");
            return Abandon(ErrorCodes.Timeout);
        }
        catch (OperationCanceledException)
        {
            Abandon(ErrorCodes.Timeout);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The type only: a message could carry what the request said.
            parts.DenyLog.Error(context.Caller.Sid, $"[error] {op} failed: {ex.GetType().Name}; {context.Caller.Describe()}");
            return Abandon(ErrorCodes.Internal);
        }
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The handler ended in the meantime.
        }
    }

    /// <summary>The kinds of grant the request accepts by the caller's lock (P9 review H1); when that cannot be read, the strictest view.</summary>
    private GrantKinds AcceptedGrants(CallerInfo caller, ValidatedRequest request) => OperationPolicy.AcceptedGrants(request, ViewOf(parts.LockViews, caller.Sid));

    /// <summary>The account's lock as the service keeps it; <see cref="LockView.Closed"/> without a source or on any failure.</summary>
    public static LockView ViewOf(Func<string, LockView>? views, string sid)
    {
        if (views is null) return LockView.Closed;
        try
        {
            return views(sid) ?? LockView.Closed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return LockView.Closed;
        }
    }

    private bool CodeEnrolled(string sid)
    {
        try
        {
            return parts.Codes.IsEnrolled(sid);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;   // unknown: fail-closed
        }
    }

    private byte[] Refuse(CallerInfo caller, Operation op, string code, string reason)
    {
        parts.DenyLog.Error(caller.Sid, $"[deny] {op} -> {code} ({reason}); {caller.Describe()}");
        parts.Events.Add(caller.Sid, "denied", $"{op}: {code}");
        return Fail(code);
    }

    public static byte[] Fail(string code) => ProtocolJson.Serialize(Response.Failure(code));

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
