using CouchHud.Core.Security;

namespace CouchHud.Core.Remote;

/// <summary>
/// A new pairing code (the QR code that lets a phone steer CouchHUD) is a parents' step with parental controls on: the parents' code for exactly
/// this one action (<see cref="Action"/>), checked before the code is made and used up once it is. Parental controls off: the unlocked couch
/// the host needs anyway (and the second factor, when it is on). The page asks for the same grant first (ui/core/net-rules.js, <c>pairingNeedsParent</c>).
/// </summary>
public static class PairingGrant
{
    public const string Action = "remotePair";

    /// <summary>Whether this call takes the parents' code: a new code with parental controls on. Showing the page asks nothing, but then shows no code of its own (<see cref="CodeToShow"/>).</summary>
    public static bool NeedsParentGrant(bool newCode, bool parentalOn) => newCode && parentalOn;

    /// <summary>
    /// The secret the pairing page may show without making a new code. Parental controls off: the current one (as before). On: only the live
    /// code that was made under the grant (<paramref name="issued"/>); anything else, including a code that was used or has run out
    /// (<paramref name="live"/> is then null), shows nothing. Never makes a code.
    /// </summary>
    public static string? CodeToShow(bool parentalOn, string? live, string? issued) =>
        live is null ? null : !parentalOn || live == issued ? live : null;

    /// <summary>The check half: null when no grant is needed (or no parents' code is set, where the gate asks for the unlock); refuses with ParentCodeNeededException otherwise.</summary>
    public static ParentTicket? Check(ParentGate gate, bool newCode, bool parentalOn) =>
        NeedsParentGrant(newCode, parentalOn) ? gate.Check(Action, null) : null;
}
