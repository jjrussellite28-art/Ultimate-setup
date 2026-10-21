using System.Text.Json;

namespace CouchHud.Service.Contracts;

/// <summary>The constants of the protocol.</summary>
public static class Protocol
{
    /// <summary>The value of the <c>v</c> field. The service answers only this version.</summary>
    public const int Version = 1;

    /// <summary>The name of the pipe (\\.\pipe\CouchHud.Service.v1).</summary>
    public const string PipeName = "CouchHud.Service.v1";

    /// <summary>Most bytes in one JSON message (not counting the length prefix). Streams have their own limits.</summary>
    public const int MaxMessageBytes = 64 * 1024;
}

/// <summary>
/// What goes over the pipe for a request: the version, the operation, an optional grant (see <see cref="GrantData"/>) and the
/// arguments as JSON, which <see cref="RequestValidator"/> reads into the type of the operation. Unknown fields are refused.
/// </summary>
public sealed record RequestEnvelope(int V, string Op, string? Grant = null, JsonElement? Args = null)
{
    // The grant and the arguments may be secrets (a password): not in ToString().
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("V = ").Append(V).Append(", Op = ").Append(Op).Append(", Grant = ").Append(Grant is null ? "none" : "***").Append(", Args = ").Append(Args is null ? "none" : "***");
        return true;
    }
}

/// <summary>An answer without data: <c>{v, ok, code}</c>.</summary>
public sealed record Response(int V, bool Ok, string Code)
{
    public static Response Success() => new(Protocol.Version, true, ErrorCodes.Ok);

    public static Response Failure(string code) => new(Protocol.Version, false, code);
}

/// <summary>An answer with the data of the operation: <c>{v, ok, code, data}</c>.</summary>
public sealed record Response<T>(int V, bool Ok, string Code, T? Data)
{
    public static Response<T> Success(T data) => new(Protocol.Version, true, ErrorCodes.Ok, data);

    public static Response<T> Failure(string code) => new(Protocol.Version, false, code, default);
}
