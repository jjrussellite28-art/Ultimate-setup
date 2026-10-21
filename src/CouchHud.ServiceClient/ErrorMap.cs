using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient;

/// <summary>What a failed call means for the shell, coarser than the codes (one text per kind).</summary>
public enum FailureKind
{
    None,

    /// <summary>busy, rateLimited: try again a little later.</summary>
    Retry,

    /// <summary>denied, lockedOut, badCode: the service refuses this caller.</summary>
    Refused,

    /// <summary>needGrant: allowed with the parents' code or an administrator (grants arrive with P9).</summary>
    NeedGrant,

    /// <summary>notImplemented, unknownOperation, badVersion: the installed service does not know this (yet): an older service.</summary>
    NotSupported,

    /// <summary>unsafeInstall: the service refuses to act because its folder is not protected.</summary>
    UnsafeInstall,

    /// <summary>A request of ours that the service found malformed (frame, JSON, argument): a bug, not the user's fault.</summary>
    Invalid,

    /// <summary>The service or the operation did not finish in time.</summary>
    Timeout,

    /// <summary>The service tried and it did not work (failed, internal, verificationFailed, notFound, unavailable, unknown codes).</summary>
    Failed,

    /// <summary>The service is not installed.</summary>
    NotInstalled,

    /// <summary>The service is installed and not running.</summary>
    NotRunning,

    /// <summary>The service could not be reached.</summary>
    Unreachable,

    /// <summary>Someone else holds the service's pipe: an attack or a broken install. Alarm.</summary>
    Squatted,

    /// <summary>The service answered against the protocol. Alarm.</summary>
    Inconsistent,

    /// <summary>This copy of CouchHUD has no service by design (not installed into a protected folder).</summary>
    NotExpected
}

/// <summary>Maps replies to <see cref="FailureKind"/> and to the key of the text the user sees (in the shell's string tables).</summary>
public static class ErrorMap
{
    public static FailureKind Classify(ServiceReply reply) => reply.Outcome switch
    {
        CallOutcome.Ok => FailureKind.None,
        CallOutcome.Refused => ClassifyCode(reply.Code),
        CallOutcome.NotInstalled => FailureKind.NotInstalled,
        CallOutcome.NotRunning => FailureKind.NotRunning,
        CallOutcome.Unreachable => FailureKind.Unreachable,
        CallOutcome.Squatted => FailureKind.Squatted,
        CallOutcome.Inconsistent => FailureKind.Inconsistent,
        CallOutcome.Timeout => FailureKind.Timeout,
        _ => FailureKind.Failed
    };

    /// <summary>The kind of a service code; codes this client does not know (a newer service) count as a plain failure.</summary>
    public static FailureKind ClassifyCode(string code) => code switch
    {
        ErrorCodes.Ok or ErrorCodes.StreamReady => FailureKind.None,
        ErrorCodes.Busy or ErrorCodes.RateLimited => FailureKind.Retry,
        ErrorCodes.Denied or ErrorCodes.LockedOut or ErrorCodes.BadCode => FailureKind.Refused,
        ErrorCodes.NeedGrant => FailureKind.NeedGrant,
        ErrorCodes.NotImplemented or ErrorCodes.UnknownOperation or ErrorCodes.BadVersion => FailureKind.NotSupported,
        ErrorCodes.UnsafeInstall => FailureKind.UnsafeInstall,
        ErrorCodes.BadFrame or ErrorCodes.TooLarge or ErrorCodes.Truncated or ErrorCodes.BadJson or ErrorCodes.UnknownField or ErrorCodes.BadArgument
            or ErrorCodes.StreamTooLarge or ErrorCodes.StreamShort => FailureKind.Invalid,
        ErrorCodes.Timeout or ErrorCodes.StreamTimeout => FailureKind.Timeout,
        _ => FailureKind.Failed
    };

    /// <summary>Whether the kind is evidence of manipulation (or a broken service): the fail-closed alarm, not just "not there".</summary>
    public static bool IsAlarm(FailureKind kind) => kind is FailureKind.Squatted or FailureKind.Inconsistent;

    /// <summary>Whether the service could not be used at all (as opposed to: it answered and refused or failed).</summary>
    public static bool IsUnavailable(FailureKind kind) =>
        kind is FailureKind.NotInstalled or FailureKind.NotRunning or FailureKind.Unreachable or FailureKind.Squatted or FailureKind.Inconsistent
            or FailureKind.UnsafeInstall or FailureKind.NotExpected;

    /// <summary>The key of the text for the user (Strings: "service.error.&lt;kind&gt;").</summary>
    public static string MessageKey(FailureKind kind) => "service.error." + kind switch
    {
        FailureKind.None => "none",
        FailureKind.Retry => "retry",
        FailureKind.Refused => "refused",
        FailureKind.NeedGrant => "needGrant",
        FailureKind.NotSupported => "notSupported",
        FailureKind.UnsafeInstall => "unsafeInstall",
        FailureKind.Invalid => "invalid",
        FailureKind.Timeout => "timeout",
        FailureKind.NotInstalled => "notInstalled",
        FailureKind.NotRunning => "notRunning",
        FailureKind.Unreachable => "unreachable",
        FailureKind.Squatted => "squatted",
        FailureKind.Inconsistent => "inconsistent",
        FailureKind.NotExpected => "notExpected",
        _ => "failed"
    };

    /// <summary>All kinds with a text (the localization test checks each key exists in both languages).</summary>
    public static IEnumerable<string> AllMessageKeys() => Enum.GetValues<FailureKind>().Select(MessageKey).Distinct();
}
