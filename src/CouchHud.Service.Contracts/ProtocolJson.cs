using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CouchHud.Service.Contracts;

/// <summary>
/// The JSON of the protocol (source generated, no reflection). Unknown members are an error everywhere, so a misspelt or
/// smuggled field never passes silently.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow)]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Response<HelloData>))]
[JsonSerializable(typeof(Response<StatusData>))]
[JsonSerializable(typeof(Response<PingData>))]
[JsonSerializable(typeof(Response<LockPoliciesData>))]
[JsonSerializable(typeof(Response<ServicesData>))]
[JsonSerializable(typeof(Response<JobStartedData>))]
[JsonSerializable(typeof(Response<JobData>))]
[JsonSerializable(typeof(Response<GrantData>))]
[JsonSerializable(typeof(Response<CodeCheckData>))]
[JsonSerializable(typeof(Response<EventsData>))]
[JsonSerializable(typeof(Response<MigrateTasksData>))]
[JsonSerializable(typeof(Response<ReleaseAllAccountsData>))]
[JsonSerializable(typeof(Response<ResetServiceStateData>))]
[JsonSerializable(typeof(ApplyLockPoliciesArgs))]
[JsonSerializable(typeof(ReleaseLockPoliciesArgs))]
[JsonSerializable(typeof(ServicesArgs))]
[JsonSerializable(typeof(SetAutoLogonArgs))]
[JsonSerializable(typeof(InstallWindowsUpdatesArgs))]
[JsonSerializable(typeof(SetRemoteFirewallArgs))]
[JsonSerializable(typeof(SetupStreamArgs))]
[JsonSerializable(typeof(PasscodeHashArgs))]
[JsonSerializable(typeof(CodeKindArgs))]
[JsonSerializable(typeof(LockModeArgs))]
[JsonSerializable(typeof(AdminApprovalArgs))]
[JsonSerializable(typeof(Response<AdminApprovalData>))]
[JsonSerializable(typeof(Response<AdminApprovalResult>))]
[JsonSerializable(typeof(VerifyCodeArgs))]
[JsonSerializable(typeof(RequestWindowsGrantArgs))]
[JsonSerializable(typeof(GetJobArgs))]
[JsonSerializable(typeof(GetRecentEventsArgs))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}

/// <summary>Builds and reads the JSON of messages (the bytes inside a frame).</summary>
public static class ProtocolJson
{
    public static byte[] SerializeRequest<TArgs>(Operation op, TArgs args, string? grant = null)
    {
        var element = JsonSerializer.SerializeToElement(args, (JsonTypeInfo<TArgs>)ProtocolJsonContext.Default.GetTypeInfo(typeof(TArgs))!);
        return JsonSerializer.SerializeToUtf8Bytes(new RequestEnvelope(Protocol.Version, op.ToString(), grant, element), ProtocolJsonContext.Default.RequestEnvelope);
    }

    /// <summary>A request without arguments.</summary>
    public static byte[] SerializeRequest(Operation op, string? grant = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new RequestEnvelope(Protocol.Version, op.ToString(), grant), ProtocolJsonContext.Default.RequestEnvelope);

    public static byte[] Serialize(Response response) => JsonSerializer.SerializeToUtf8Bytes(response, ProtocolJsonContext.Default.Response);

    public static byte[] Serialize<T>(Response<T> response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, (JsonTypeInfo<Response<T>>)ProtocolJsonContext.Default.GetTypeInfo(typeof(Response<T>))!);

    public static Response<T>? DeserializeResponse<T>(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<Response<T>>)ProtocolJsonContext.Default.GetTypeInfo(typeof(Response<T>))!);

    public static Response? DeserializeResponse(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.Response);
}
