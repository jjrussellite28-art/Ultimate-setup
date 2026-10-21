namespace CouchHud.Service.Contracts;

/// <summary>The <c>code</c> of a response: "ok" or why not. Strings on the wire, so a newer service can add codes.</summary>
public static class ErrorCodes
{
    public const string Ok = "ok";
    /// <summary>The ok answer to a request that is followed by a stream of bytes: send them now.</summary>
    public const string StreamReady = "streamReady";

    // The message itself is wrong.
    public const string BadFrame = "badFrame";
    public const string TooLarge = "tooLarge";
    public const string Truncated = "truncated";
    public const string BadJson = "badJson";
    public const string BadVersion = "badVersion";
    public const string UnknownOperation = "unknownOperation";
    public const string UnknownField = "unknownField";
    public const string BadArgument = "badArgument";

    // The stream of bytes is wrong.
    public const string StreamTooLarge = "streamTooLarge";
    public const string StreamShort = "streamShort";
    public const string StreamTimeout = "streamTimeout";

    // The service says no.
    public const string Denied = "denied";
    public const string NeedGrant = "needGrant";
    public const string BadCode = "badCode";
    public const string LockedOut = "lockedOut";
    public const string RateLimited = "rateLimited";
    public const string Busy = "busy";
    public const string UnsafeInstall = "unsafeInstall";
    public const string NotFound = "notFound";
    public const string Unavailable = "unavailable";
    public const string VerificationFailed = "verificationFailed";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
    /// <summary>The operation is known and allowed, but this service version does not carry it out (yet).</summary>
    public const string NotImplemented = "notImplemented";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Ok, StreamReady, BadFrame, TooLarge, Truncated, BadJson, BadVersion, UnknownOperation, UnknownField, BadArgument,
        StreamTooLarge, StreamShort, StreamTimeout, Denied, NeedGrant, BadCode, LockedOut, RateLimited, Busy, UnsafeInstall,
        NotFound, Unavailable, VerificationFailed, Failed, Timeout, Internal, NotImplemented
    };
}
