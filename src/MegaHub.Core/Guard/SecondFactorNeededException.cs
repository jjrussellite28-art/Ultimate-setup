namespace MegaHub.Core.Guard;

/// <summary>
/// The action needs the second factor (ab 0.7): the code was accepted, now a paired phone has to confirm. The bridge tells the page
/// ("twofa": the request id, the number the TV shows, the action's text, when it ends, whether a phone can be reached, the administrator's
/// emergency way); the page shows the question, waits for the confirmation (or the administrator's), then repeats the same call, and the
/// host uses the ticket up. A normal refusal for everything that still derives from <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class SecondFactorNeededException(string message, string requestId, int number, string action, string label, long leftMs, bool reachable, int lockedOutSeconds)
    : InvalidOperationException(message)
{
    public string RequestId { get; } = requestId;
    public int Number { get; } = number;
    public string Action { get; } = action;
    public string Label { get; } = label;
    public long LeftMs { get; } = leftMs;

    /// <summary>At least one phone that may confirm has an open event stream right now (it is a hint for the page, not a promise).</summary>
    public bool Reachable { get; } = reachable;

    /// <summary>More than 0: too many failed tries, only the administrator's way works for this long (no question was asked).</summary>
    public int LockedOutSeconds { get; } = lockedOutSeconds;

    /// <summary>More than 0: a question was asked a moment ago, a new one comes in this many seconds (no question exists, nothing to confirm yet).</summary>
    public int ThrottledSeconds { get; init; }

    /// <summary>The line under the action (a profile, a rule, a plugin), cleaned.</summary>
    public string Detail { get; init; } = "";
}
