using System.Net;
using System.Net.Http.Headers;

namespace CouchHud.Core.Net;

/// <summary>How hard one host may be hit: parallel requests and the gap between two request starts.</summary>
public sealed record HostPolicy(int MaxParallel, TimeSpan MinInterval);

/// <summary>
/// Puts every request to a third-party host on a leash: a limited number in parallel, a minimum gap
/// between starts, Retry-After on 429/503, exponential backoff, and a circuit breaker that stops
/// asking a host that keeps failing (so a flaky network or a rate limit never ends in a ban).
/// Failures surface as HttpRequestException with the status code, like HttpClient does itself.
/// </summary>
public sealed class PoliteHandler : DelegatingHandler
{
    /// <summary>Longest wait inside one request; anything longer fails fast and the caller tries again later.</summary>
    public static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(30);

    private const int MaxRetries = 2;
    private const int BreakerThreshold = 5;
    private static readonly TimeSpan BreakerFirst = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BreakerMax = TimeSpan.FromHours(1);

    private readonly Func<string, HostPolicy> _policy;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _userAgent;
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    private sealed class HostState(int parallel)
    {
        public readonly SemaphoreSlim Slots = new(parallel, parallel);
        public readonly object Lock = new();
        public DateTimeOffset NextStart;
        public int Failures;
        public DateTimeOffset OpenUntil;
        public TimeSpan OpenFor = BreakerFirst;
    }

    public PoliteHandler(HttpMessageHandler inner, string version, Func<string, HostPolicy>? policy = null,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : base(inner)
    {
        _policy = policy ?? DefaultPolicy;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _userAgent = $"MegaHub/{version} (+https://github.com/jjrussellite28-art/Ultimate-setup)";
    }

    /// <summary>Store and API hosts get one request at a time and a generous gap; CDNs may take two.</summary>
    public static HostPolicy DefaultPolicy(string host)
    {
        if (host.EndsWith("steamgriddb.com", StringComparison.OrdinalIgnoreCase)) return new HostPolicy(1, TimeSpan.FromMilliseconds(600));
        if (host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase) || host.Equals("api.steampowered.com", StringComparison.OrdinalIgnoreCase)) return new HostPolicy(1, TimeSpan.FromMilliseconds(1500));
        return new HostPolicy(2, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>True while the host is paused by the circuit breaker.</summary>
    public bool IsOpen(string host)
    {
        HostState state;
        lock (_hosts)
        {
            if (!_hosts.TryGetValue(host, out state!)) return false;
        }
        lock (state.Lock) return state.OpenUntil > _now();
    }

    private HostState StateOf(string host)
    {
        lock (_hosts)
        {
            if (!_hosts.TryGetValue(host, out var state))
            {
                state = new HostState(Math.Max(1, _policy(host).MaxParallel));
                _hosts[host] = state;
            }
            return state;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("request without address");
        var host = uri.Host;
        var state = StateOf(host);
        var policy = _policy(host);
        if (request.Headers.UserAgent.Count == 0) request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        var current = request;
        for (var attempt = 0; ; attempt++)
        {
            lock (state.Lock)
            {
                if (state.OpenUntil > _now())
                    throw new HttpRequestException($"{host} is paused after repeated errors", null, HttpStatusCode.ServiceUnavailable);
            }

            HttpResponseMessage response;
            await state.Slots.WaitAsync(ct);
            try
            {
                TimeSpan gap;
                lock (state.Lock)
                {
                    var now = _now();
                    var start = state.NextStart > now ? state.NextStart : now;
                    state.NextStart = start + policy.MinInterval;
                    gap = start - now;
                }
                if (gap > TimeSpan.Zero) await _delay(gap, ct);

                try
                {
                    response = await base.SendAsync(current, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    Failed(state, null);
                    throw;
                }
            }
            finally
            {
                state.Slots.Release();
            }

            var code = response.StatusCode;
            if (code != HttpStatusCode.TooManyRequests && code != HttpStatusCode.ServiceUnavailable)
            {
                if ((int)code >= 500) Failed(state, null);
                else Succeeded(state);
                return response;
            }

            var retryAfter = RetryAfterOf(response.Headers.RetryAfter);
            var wait = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            // Whoever asks next honors the same pause.
            lock (state.Lock)
            {
                var until = _now() + wait;
                if (until > state.NextStart) state.NextStart = until;
            }
            if (attempt >= MaxRetries || wait > MaxInlineWait || request.Content is not null)
            {
                Failed(state, retryAfter);
                return response;
            }

            response.Dispose();
            await _delay(wait, ct);
            // A request message can't be sent twice.
            current = Clone(request);
        }
    }

    /// <summary>No server gets to park us for longer than this (also keeps date arithmetic far from overflow).</summary>
    private static readonly TimeSpan MaxPause = TimeSpan.FromHours(1);

    private TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        TimeSpan? wait = null;
        if (header.Delta is { } delta) wait = delta;
        else if (header.Date is { } date) wait = date - _now();
        if (wait is not { } w) return null;
        return w < TimeSpan.Zero ? TimeSpan.Zero : w > MaxPause ? MaxPause : w;
    }

    private void Succeeded(HostState state)
    {
        lock (state.Lock)
        {
            state.Failures = 0;
            state.OpenFor = BreakerFirst;
        }
    }

    /// <summary>A long Retry-After opens the breaker for exactly that long; plain errors do after five in a row.</summary>
    private void Failed(HostState state, TimeSpan? retryAfter)
    {
        lock (state.Lock)
        {
            state.Failures++;
            if (retryAfter is { } pause && pause > MaxInlineWait)
            {
                state.OpenUntil = _now() + pause;
                return;
            }
            if (state.Failures < BreakerThreshold) return;
            state.OpenUntil = _now() + state.OpenFor;
            state.OpenFor = TimeSpan.FromTicks(Math.Min(state.OpenFor.Ticks * 2, BreakerMax.Ticks));
            state.Failures = 0;
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }
}
