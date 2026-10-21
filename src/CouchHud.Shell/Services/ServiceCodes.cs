using CouchHud.Core.Config;
using CouchHud.Core.Localization;
using CouchHud.Core.Security;
using CouchHud.Service.Contracts;
using CouchHud.ServiceClient;
using ServiceCodeState = CouchHud.ServiceClient.HeldCodes;

namespace CouchHud.Shell.Services;

/// <summary>
/// The codes and the lock mode with the CouchHUD service (P9): the service keeps them and checks the buttons; the config keeps only
/// <see cref="Passcode.HeldByService"/> (the UI needs to know that a code exists, nothing more). Without a service by design (an unprotected
/// or development copy) the config keeps the hash as before and the shell checks it itself.
/// </summary>
public static class ServiceCodes
{
    /// <summary>Whether codes go through the service (the installed copy).</summary>
    public static bool Expected => AdminService.Ops.Expected;

    /// <summary>What the service said last about this account's codes and mode (settings page, setup assistant); null: not asked yet.</summary>
    public static ServiceCodeState? Known { get; private set; }

    /// <summary>Asks the service which codes it holds (in the background of the service watch).</summary>
    public static async Task<ServiceCodeState?> RefreshAsync()
    {
        if (!Expected) return null;
        var codes = await AdminService.Ops.CodeStatusAsync().ConfigureAwait(false);
        if (codes is not null) Known = codes;
        return codes;
    }

    /// <summary>The service holds a code of its own, so the local one was not taken over (the settings and the setup assistant say so).</summary>
    public static bool Conflict { get; private set; }

    /// <summary>
    /// The one-time migration of the local hash and lock mode (see <see cref="CodeFlow.MigrateAsync"/>). The service cannot know whether a
    /// program of the user changed the config before; it takes it once per account, only a hash in the format of <see cref="Passcode.Hash"/>,
    /// and never over a code or a lock it has. Taken: the config keeps only the marker. A conflict (the service has its own) is reported, not
    /// treated as success (review L-c): the local hash stays, unlocking goes by the service. Nothing to migrate counts as done.
    /// </summary>
    public static async Task<MigrationResult> MigrateAsync(ConfigStore config)
    {
        if (!Expected) return MigrationResult.NotYet;
        var hash = config.Current.PasscodeHash;
        var mode = config.Current.CouchLock;
        var local = Passcode.IsLocalHash(hash);
        if (!local && mode != LockModes.Windows) return MigrationResult.Done;
        var result = await CodeFlow.MigrateAsync(AdminService.Ops, local ? hash : null, mode).ConfigureAwait(false);
        Console.WriteLine($"[codes] local code and lock mode to the service: {result}");
        if (result == MigrationResult.Conflict) Conflict = true;
        if (result == MigrationResult.Done && local && mode != LockModes.Windows)
        {
            config.Update(c =>
            {
                if (c.PasscodeHash == hash) c.PasscodeHash = Passcode.HeldByService;
            });
        }
        return result;
    }

    /// <summary>
    /// The lock mode changes (P9 review H1): raising is free; lowering needs the current mode's grant (the unlock just done), the parents'
    /// approval or, as the fallback, an administrator's. Throws with the reason when the service did not take it.
    /// </summary>
    public static async Task SetModeAsync(string mode, IReadOnlyList<string>? sequence = null, bool parental = false)
    {
        // From no lock to the code lock the code comes along in the same step (a code enrolled on its own would not survive the switch).
        var hash = sequence is null ? null : Passcode.Hash(sequence);
        var kind = parental ? CodeKinds.Both : CodeKinds.Couch;
        var result = await AdminService.WithGrantAsync(Operation.SetLockMode, () => AdminService.Ops.SetLockModeAsync(mode, hash, kind)).ConfigureAwait(false);
        Console.WriteLine($"[codes] lock mode {mode}: {result}");
        if (!result.Ok) throw new InvalidOperationException(Strings.Get("codes.modeNotStored", AdminService.Message(result)));
        if (hash is not null) Conflict = false;
    }

    /// <summary>
    /// A code goes from the service (the couch lock or the parental controls are switched off). Nothing to remove counts as done. Throws with the
    /// reason when the service refused (the switch then does not happen).
    /// </summary>
    public static async Task RemoveAsync(string kind)
    {
        var result = await AdminService.WithGrantAsync(Operation.RemovePasscode, () => AdminService.Ops.RemovePasscodeAsync(kind)).ConfigureAwait(false);
        Console.WriteLine($"[codes] {kind} code removed from the service: {result}");
        // "denied" from the state check: there is no such code.
        if (!result.Ok && result.Code != ErrorCodes.Denied) throw new InvalidOperationException(Strings.Get("codes.notRemoved", AdminService.Message(result)));
    }

    /// <summary>
    /// A new code: as the couch code and, with parental controls, as couch and parents' code in one step (the HUD uses one code for both).
    /// Throws with the reason when the service did not take it.
    /// </summary>
    public static async Task StoreAsync(IReadOnlyList<string> sequence, bool parental)
    {
        var result = await CodeFlow.StoreAsync(AdminService.Ops, Passcode.Hash(sequence), parental, AdminService.WithGrantAsync).ConfigureAwait(false);
        Console.WriteLine($"[codes] new code to the service: {result}");
        if (!result.Ok) throw new InvalidOperationException(Strings.Get("codes.notStored", AdminService.Message(result)));
        Conflict = false;
    }

}
