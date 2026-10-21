namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// Tells CaseHUD while a game runs (POST /gaming is a lease of 120 s that has to be renewed): sent at the start,
/// renewed every 60 s by <see cref="TickAsync"/>, ended at once with active=false. Pure logic with an injected clock;
/// the shell calls <see cref="TickAsync"/> every few seconds. A failed send is tried again after 10 s, so a CaseHUD
/// that starts (or restarts) during a game still hears about it. Begin, Tick and End never run side by side: a Tick that
/// is still sending cannot bring the lease back after the game ended.
/// </summary>
public sealed class CaseHudGamingLease(Func<CaseHudClient?> client, Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan Renew = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);
    public const int LeaseSeconds = 120;

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _active;
    private string? _game;
    private int? _pid;
    private DateTimeOffset _next;

    public bool Active => _active;

    public async Task BeginAsync(string game, int? pid = null)
    {
        await _gate.WaitAsync();
        try
        {
            _active = true;
            _game = game;
            _pid = pid;
            _next = DateTimeOffset.MinValue;
            await SendIfDueAsync();
        }
        finally { _gate.Release(); }
    }

    /// <summary>The game is over: end the lease (best effort, CaseHUD lets it expire after 120 s anyway).</summary>
    public Task EndAsync() => EndAsync(TimeSpan.FromSeconds(10));

    /// <summary>Same, but gives up after <paramref name="timeout"/> (shutting down must not wait for a CaseHUD that hangs).</summary>
    public async Task EndAsync(TimeSpan timeout)
    {
        if (!_active) return;
        // From here on no renewal starts any more, even before the send below.
        _active = false;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _gate.WaitAsync(cts.Token);
            try
            {
                if (client() is { } c) await c.GamingAsync(false, ct: cts.Token);
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) when (ex is CaseHudException or OperationCanceledException) { }
    }

    public async Task TickAsync()
    {
        if (!_active) return;
        await _gate.WaitAsync();
        try
        {
            // Re-check: the game may have ended while this waited for the gate.
            if (_active) await SendIfDueAsync();
        }
        finally { _gate.Release(); }
    }

    private async Task SendIfDueAsync()
    {
        if (_now() < _next) return;
        try
        {
            if (client() is not { } c)
            {
                _next = _now() + Retry;
                return;
            }
            await c.GamingAsync(true, _game, _pid, LeaseSeconds);
            _next = _now() + Renew;
        }
        catch (CaseHudException)
        {
            _next = _now() + Retry;
        }
    }
}
