namespace CouchHud.Service.Dispatch;

/// <summary>
/// The handler tasks that are running, per account and in total, including the ones whose caller already got "timeout": a
/// handler that ignores its cancellation keeps its place until it really ends, so abandoned work cannot pile up and an account
/// cannot use timeouts to take every slot.
/// </summary>
public sealed class RunningWork(int perAccount, int total)
{
    private readonly Dictionary<string, int> _bySid = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private int _total;

    public int PerAccount { get; } = perAccount;

    public int Total { get; } = total;

    /// <summary>A lease to dispose when the work has really ended, or null when the account or the service is at its limit.</summary>
    public IDisposable? TryStart(string sid)
    {
        lock (_lock)
        {
            _bySid.TryGetValue(sid, out var count);
            if (count >= PerAccount || _total >= Total) return null;
            _bySid[sid] = count + 1;
            _total++;
        }
        return new Lease(this, sid);
    }

    public int RunningFor(string sid)
    {
        lock (_lock) return _bySid.GetValueOrDefault(sid);
    }

    private void End(string sid)
    {
        lock (_lock)
        {
            if (!_bySid.TryGetValue(sid, out var count)) return;
            if (count <= 1) _bySid.Remove(sid);
            else _bySid[sid] = count - 1;
            _total--;
        }
    }

    private sealed class Lease(RunningWork owner, string sid) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.End(sid);
        }
    }
}
