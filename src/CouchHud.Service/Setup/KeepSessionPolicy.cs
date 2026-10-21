using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Setup;

/// <summary>
/// Whether a CouchHUD update may keep the caller's session connected (see <see cref="SignedSetupPipeline.KeepSessionArgument"/>):
/// only when the couch of that account cannot be locked, or the caller is an elevated administrator, or brings a valid grant (the
/// shell sends the one of its unlock). The service takes "a code is enrolled" (<see cref="ICodeState"/>, the service's own code store)
/// as "the couch may be locked". Fail-closed: an error in asking counts as "may be locked".
/// </summary>
public sealed class KeepSessionPolicy(ICodeState codes, IGrantStore grants, Func<string, LockView>? views = null)
{
    /// <summary>Keeps no session (every caller's session is disconnected during the update).</summary>
    public static readonly KeepSessionPolicy Never = new(AssumeCodeEnrolled.Instance, NoGrants.Instance);

    public bool MayKeep(CallerInfo caller, ValidatedRequest request)
    {
        if (caller.IsElevatedAdmin) return true;
        bool enrolled;
        try
        {
            enrolled = codes.IsEnrolled(caller.Sid);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            enrolled = true;
        }
        if (!enrolled) return true;
        // A grant is only asked for (and counted) when it is needed: the kinds that open this account's couch (by its lock mode).
        return request.Grant is { } grant
            && grants.TryConsume(caller, grant, request.Operation, OperationPolicy.AcceptedGrants(request, Dispatch.Dispatcher.ViewOf(views, caller.Sid)), out _);
    }
}
