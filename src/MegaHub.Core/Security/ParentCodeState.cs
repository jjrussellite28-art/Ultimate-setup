using MegaHub.Core.Config;

namespace MegaHub.Core.Security;

/// <summary>
/// Whether the parents' releases are protected by a code at all. Without protection <see cref="ParentGate"/> falls back to the unlocked couch,
/// so a state that only looks "no code" must not be read as one: after the administrator's reset of a damaged code file the service holds no
/// code, but a parent set the controls up, so the releases stay protected and only an administrator's approval gives them until a new code is set.
/// </summary>
public static class ParentCodeState
{
    public static bool Has(string couchLock, string passcodeHash, bool windowsPasswordSet, bool parentalEnabled, bool resetPending) =>
        couchLock == "code" && (passcodeHash.Length > 0 || parentalEnabled && resetPending)
        || couchLock == "windows" && windowsPasswordSet;

    /// <summary>
    /// A code (or the service's marker of one) is stored: a non-empty value ends the reset's marker, an empty one (code removed) leaves it.
    /// <paramref name="parentCodePresent"/>: the service holds a parents' code too; a couch code alone (a kept couch code) does not end the marker.
    /// </summary>
    public static void CodeIsSet(AppConfig c, string hash, bool parentCodePresent = true)
    {
        c.PasscodeHash = hash;
        if (hash.Length > 0 && parentCodePresent) c.ParentCodeResetPending = false;
    }

    /// <summary>
    /// The administrator's reset of the damaged code file ran, in ONE write of the configuration (a crash between two writes must not leave the
    /// hash cleared without the marker): a couch code the service held is forgotten (<paramref name="heldMarker"/> is the config's marker of it),
    /// and with parental controls on and no parents' code left the parents' releases stay protected until a new code is set.
    /// </summary>
    public static void ApplyReset(AppConfig c, bool couchCodeGone, bool parentCodeGone, string heldMarker)
    {
        if (couchCodeGone && c.PasscodeHash == heldMarker) c.PasscodeHash = "";
        if (c.ParentalEnabled && parentCodeGone) c.ParentCodeResetPending = true;
    }
}
