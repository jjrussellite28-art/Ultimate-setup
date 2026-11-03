using System.Security.Cryptography;
using System.Text;

namespace CouchHud.Core.Remote;

/// <summary>What a phone's answer to the question led to.</summary>
public enum SecondFactorAnswer
{
    /// <summary>The right number: the action may run once.</summary>
    Confirmed,
    /// <summary>A wrong number: the question is over, the phone way is locked for a while (<see cref="SecondFactorChallenges.Backoff"/>).</summary>
    Wrong,
    /// <summary>Not asked any more (older than 60 s, answered, cancelled, replaced by another action or the HUD restarted).</summary>
    Unknown,
    /// <summary>This phone may not confirm (not Full, a child's phone, no channel).</summary>
    NotEligible,
    /// <summary>Too many failed tries: only a Windows administrator's approval for a while.</summary>
    LockedOut
}

/// <summary>
/// The question as the TV shows it (with the number). The phone gets the action, the detail and the time, never the number and no options:
/// it has to type the number it reads on the TV.
/// </summary>
public sealed record SecondFactorQuestion(string RequestId, string Action, string Label, string Detail, int Number, DateTimeOffset Asked, DateTimeOffset Expires);

/// <summary>The outcome of asking: the question, or how long new questions are held back (<see cref="SecondFactorChallenges.AskGap"/>).</summary>
public sealed record SecondFactorAsk(SecondFactorQuestion? Question, TimeSpan Throttled);

/// <summary>
/// The second factor's state machine, pure: one question at a time, asked after the code was accepted, answered on a phone. The question is
/// bound to the action, a hash of its parameters and a random request id; it lives 60 seconds, ends with its first answer (right or wrong)
/// and is gone after a restart (memory only). The TV shows a two-digit number and the phone must type it (a keypad: no options to tap, so a
/// blind tap finds nothing); a wrong number ends the question and locks the phone way (1 minute, then 10 minutes, then an hour; a right
/// answer starts over). New questions come at most every 10 seconds. A right answer becomes a ticket that exactly one execution of exactly
/// this action with exactly these parameters can use up within 60 seconds, as long as the confirming phone is still allowed.
/// </summary>
public sealed class SecondFactorChallenges
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>At most one new question per this time (also against flooding the phones with questions).</summary>
    public static readonly TimeSpan AskGap = TimeSpan.FromSeconds(10);

    /// <summary>The phone way is shut for this long after the n-th wrong answer in a row (1, 2, then 3 or more).</summary>
    public static TimeSpan Backoff(int strikes) => strikes switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromHours(1)
    };

    /// <summary>The device name of a ticket the Windows administrator made (no phone to ask again).</summary>
    public const string AdminDevice = "#admin";

    private sealed record Ticket(string Action, string ParamHash, string Device, DateTimeOffset Until);

    /// <summary>All phones together: this many failed answers within <see cref="HardWindow"/> shut the phone way for everybody for <see cref="HardLock"/>.</summary>
    public const int HardLimit = 10;

    public static readonly TimeSpan HardWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan HardLock = TimeSpan.FromHours(1);

    private sealed record DeviceState(int Strikes, DateTimeOffset Until);

    private sealed record State(Dictionary<string, DeviceState> Devices, List<DateTimeOffset> Failures, DateTimeOffset GlobalUntil);

    private readonly Func<DateTimeOffset> now;
    private readonly Func<int, int> _random;
    private readonly string? _stateFile;
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _failures = [];
    private DateTimeOffset _globalUntil = DateTimeOffset.MinValue;
    private SecondFactorQuestion? _current;
    private string _currentHash = "";
    private readonly List<Ticket> _tickets = [];
    private DateTimeOffset _lastNew = DateTimeOffset.MinValue;

    /// <param name="now">The clock (the HUD's monotonic one).</param>
    /// <param name="random">Numbers below the given bound (tests); null: the system's random generator.</param>
    /// <param name="stateFile">
    /// Where the failed tries and lock-outs are kept, so a restart is no way to start over. A file that exists but cannot be read (nor its copy)
    /// shuts the phone way for an hour (fail closed); no file is a fresh start. null: memory only (tests).
    /// </param>
    public SecondFactorChallenges(Func<DateTimeOffset> now, Func<int, int>? random = null, string? stateFile = null)
    {
        this.now = now;
        _random = random ?? RandomNumberGenerator.GetInt32;
        _stateFile = stateFile;
        Load();
    }

    private void Load()
    {
        if (_stateFile is null || !File.Exists(_stateFile) && !File.Exists(Files.DurableFile.BackupOf(_stateFile))) return;
        var text = Files.DurableFile.ReadValid(_stateFile, t => TryParse(t) is not null, out _);
        if (text is not null && TryParse(text) is { } state)
        {
            var latest = now() + HardLock;
            foreach (var (id, device) in state.Devices ?? [])
                if (!string.IsNullOrEmpty(id)) _devices[id] = new DeviceState(Math.Max(0, device.Strikes), device.Until > latest ? latest : device.Until);
            // A failed try stamped in the future (the clock went back, or the file was edited) still counts: capped to now, never dropped.
            _failures.AddRange((state.Failures ?? []).Select(f => f > now() ? now() : f).Where(f => now() - f < HardWindow));
            _globalUntil = state.GlobalUntil > latest ? latest : state.GlobalUntil;
            return;
        }
        // Neither the file nor its copy can be read: no fresh start (fail closed), the phone way is shut for an hour.
        _globalUntil = now() + HardLock;
    }

    private static State? TryParse(string text)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<State>(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void Save()
    {
        if (_stateFile is null) return;
        try
        {
            Files.DurableFile.WriteAllText(_stateFile, System.Text.Json.JsonSerializer.Serialize(new State(new Dictionary<string, DeviceState>(_devices), [.. _failures], _globalUntil)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The count lives on in memory; a restart then starts over, which the next wrong answer undoes (documented in SECURITY.md).
        }
    }

    /// <summary>SHA-256 over the action and its parameters, length-prefixed so that no two parameter lists give the same text.</summary>
    public static string Bind(string action, params string?[] parameters)
    {
        var text = new StringBuilder().Append(action.Length).Append(':').Append(action);
        // null has its own tag (-1), so a ticket for "no value" can never apply to the empty text and the other way round.
        foreach (var p in parameters) text.Append('|').Append(p is null ? -1 : p.Length).Append(':').Append(p);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>Several steps of one change as one binding (the integration mode switches shell, lock and Task Manager together).</summary>
    public static string BindAll(params string[] hashes) => Bind("set", hashes);

    /// <summary>The phone way is shut for everybody (the hard limit across all phones, or an unreadable state file).</summary>
    public bool IsLockedOut
    {
        get
        {
            lock (_lock) return now() < _globalUntil;
        }
    }

    /// <summary>How long this phone cannot answer: its own lock-out or the global one, whichever is longer.</summary>
    public TimeSpan LockedForDevice(string deviceId)
    {
        lock (_lock) return DeviceWait(deviceId);
    }

    /// <summary>
    /// For the TV: how long until a phone of <paramref name="deviceIds"/> can answer (the shortest wait of them; the global lock-out for all). Zero
    /// when one is free. With no phone at all, only the global lock-out counts.
    /// </summary>
    public TimeSpan LockedFor(IEnumerable<string> deviceIds)
    {
        lock (_lock)
        {
            var ids = deviceIds.ToList();
            if (ids.Count == 0) return Positive(_globalUntil - now());
            return ids.Min(DeviceWait);
        }
    }

    private TimeSpan DeviceWait(string deviceId)
    {
        var own = _devices.TryGetValue(deviceId, out var d) ? d.Until : DateTimeOffset.MinValue;
        return Positive((own > _globalUntil ? own : _globalUntil) - now());
    }

    private static TimeSpan Positive(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.Zero;

    /// <summary>
    /// Uses up the ticket of exactly this action and these parameters; false when there is none (never asked, other parameters, used, older
    /// than 60 s) or when <paramref name="deviceOk"/> says the confirming phone may not confirm any more (unpaired, lowered, a child's profile
    /// now). Tickets of other bindings stay.
    /// </summary>
    public bool TryUse(string action, string paramHash, Func<string, bool> deviceOk)
    {
        lock (_lock)
        {
            _tickets.RemoveAll(t => now() >= t.Until);
            var found = _tickets.FindIndex(t => t.Action == action && t.ParamHash == paramHash);
            if (found < 0) return false;
            var ticket = _tickets[found];
            _tickets.RemoveAt(found);
            return ticket.Device == AdminDevice || deviceOk(ticket.Device);
        }
    }

    /// <summary>Whether a ticket of this binding waits and its phone is still fine (not used up).</summary>
    public bool HasTicket(string action, string paramHash, Func<string, bool> deviceOk)
    {
        lock (_lock)
        {
            _tickets.RemoveAll(t => now() >= t.Until);
            return _tickets.Any(t => t.Action == action && t.ParamHash == paramHash && (t.Device == AdminDevice || deviceOk(t.Device)));
        }
    }

    /// <summary>
    /// Asks the question for this binding. The same binding while its question is alive gets that question back (the page repeating the call);
    /// another binding replaces it (one question at a time: the old request id is dead and its answer counts for nothing), but not sooner than
    /// <see cref="AskGap"/> after the last new question (<see cref="SecondFactorAsk.Throttled"/>). While the phone way is locked out the question is
    /// still asked (the administrator's way needs it); every phone answer is refused (<see cref="SecondFactorAnswer.LockedOut"/>).
    /// </summary>
    public SecondFactorAsk Ask(string action, string paramHash, string label, string detail = "")
    {
        lock (_lock)
        {
            Expire();
            if (_current is not null && _current.Action == action && _currentHash == paramHash) return new(_current, TimeSpan.Zero);
            var since = now() - _lastNew;
            if (since < AskGap && since >= TimeSpan.Zero) return new(null, AskGap - since);
            var number = 10 + _random(90);
            var asked = now();
            _lastNew = asked;
            _currentHash = paramHash;
            return new(_current = new SecondFactorQuestion(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), action, label, detail, number, asked, asked + Lifetime), TimeSpan.Zero);
        }
    }

    /// <summary>The live question (for the TV page), or null.</summary>
    public SecondFactorQuestion? Current
    {
        get
        {
            lock (_lock)
            {
                Expire();
                return _current;
            }
        }
    }

    /// <summary>The page gave up: the request id is dead.</summary>
    public void Cancel(string? requestId)
    {
        lock (_lock)
        {
            if (_current is not null && _current.RequestId == requestId) _current = null;
        }
    }

    /// <summary>
    /// Everything open goes: the question and the tickets (a restart builds a new object anyway; also when the second factor is switched off, a
    /// phone is unpaired or lowered, a profile switches or PC mode starts). The failed tries and the throttle stay: that is no way to start over.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _current = null;
            _tickets.Clear();
        }
    }

    /// <summary>Forgets the failed tries too (the second factor was switched off on purpose).</summary>
    public void ResetAll()
    {
        lock (_lock)
        {
            _current = null;
            _tickets.Clear();
            _devices.Clear();
            _failures.Clear();
            _globalUntil = DateTimeOffset.MinValue;
            _lastNew = DateTimeOffset.MinValue;
            Save();
        }
    }

    /// <summary>
    /// A phone types a number: <paramref name="eligible"/> is the host's decision for this phone right now. Whatever the outcome, the question
    /// is over (right: a ticket exists; wrong: counted and locked; not eligible: untouched, nothing learnt). Only a question that is current,
    /// alive and asked by this very request id can be answered; the second answer of the same id finds nothing.
    /// </summary>
    public SecondFactorAnswer Answer(string? requestId, string deviceId, int typed, bool eligible)
    {
        lock (_lock)
        {
            if (DeviceWait(deviceId) > TimeSpan.Zero) return SecondFactorAnswer.LockedOut;
            Expire();
            if (_current is null || requestId is null || !FixedEquals(_current.RequestId, requestId)) return SecondFactorAnswer.Unknown;
            if (!eligible) return SecondFactorAnswer.NotEligible;
            var question = _current;
            _current = null;
            if (typed != question.Number)
            {
                // The phone's own count and lock-out (a foreign phone cannot shut the parents' phone out), and the hard limit over all phones.
                var strikes = (_devices.TryGetValue(deviceId, out var own) ? own.Strikes : 0) + 1;
                _devices[deviceId] = new DeviceState(strikes, now() + Backoff(strikes));
                _failures.RemoveAll(f => now() - f >= HardWindow);
                _failures.Add(now());
                if (_failures.Count >= HardLimit)
                {
                    _globalUntil = now() + HardLock;
                    _failures.Clear();
                }
                Save();
                return SecondFactorAnswer.Wrong;
            }
            if (_devices.Remove(deviceId)) Save();
            _tickets.Add(new Ticket(question.Action, _currentHash, deviceId, now() + Lifetime));
            return SecondFactorAnswer.Confirmed;
        }
    }

    /// <summary>
    /// A Windows administrator approved (UAC, the emergency way) for the live question: its ticket is made. The administrator's approval is
    /// worth more than a phone, so it does not count as a failed try and ignores the lock-out of the phone way; it starts the count over.
    /// </summary>
    public bool ApproveByAdmin(string? requestId)
    {
        lock (_lock)
        {
            Expire();
            if (_current is null || requestId is null || !FixedEquals(_current.RequestId, requestId)) return false;
            _tickets.Add(new Ticket(_current.Action, _currentHash, AdminDevice, now() + Lifetime));
            _current = null;
            // A Windows administrator is worth more than any phone: the failed tries of every phone start over.
            _devices.Clear();
            _failures.Clear();
            _globalUntil = DateTimeOffset.MinValue;
            Save();
            return true;
        }
    }

    private void Expire()
    {
        if (_current is not null && now() >= _current.Expires) _current = null;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
