namespace CouchHud.Core.Security;

/// <summary>
/// The decisions around the recovery (the way back to the normal Windows desktop) as plain functions. The recovery
/// is reachable by anyone at the controller (LB+RB+View, three power cuts), so it works in two steps: first only lock
/// Windows and wait for the sign-in of the account, then, and only then, take the shell and the automatic
/// sign-in away. A restart at the lock screen leads back into the couch.
/// </summary>
public static class RecoveryRules
{
    /// <summary>The couch lock is set up: the recovery waits for the Windows sign-in before it touches shell and autologin.</summary>
    public static bool MustAuthenticateFirst(string couchLock) => !string.Equals(couchLock, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// While the couch lock and an automatic sign-in are on and CouchHud is not the shell (so nothing locks the couch
    /// behind that sign-in), a Run entry of the account locks Windows right at the sign-in.
    /// </summary>
    public static bool WantsSignInLock(string couchLock, bool autoLogonOn, bool installedAsShell) =>
        MustAuthenticateFirst(couchLock) && autoLogonOn && !installedAsShell;

    /// <summary>An automatic sign-in follows the start of its session within this time; a person needs longer (typing, face, finger).</summary>
    public static readonly TimeSpan AutoSignInWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Was the session signed in automatically? Compares when the session was connected with when the user logged on: a short gap
    /// is the automatic sign-in. A logon before the connect time is a reconnect (an older session picked up again), not a sign-in.
    /// </summary>
    public static bool LooksLikeAutoSignIn(DateTime connectUtc, DateTime logonUtc)
    {
        var gap = logonUtc - connectUtc;
        return gap >= TimeSpan.FromSeconds(-2) && gap < AutoSignInWindow;
    }

    /// <summary>Tries <paramref name="attempt"/> up to <paramref name="attempts"/> times with <paramref name="pause"/> in between; true on the first success.</summary>
    public static bool Repeat(Func<bool> attempt, int attempts, Action pause)
    {
        for (var i = 0; i < Math.Max(1, attempts); i++)
        {
            if (attempt()) return true;
            if (i < attempts - 1) pause();
        }
        return false;
    }

    /// <summary>
    /// Parental controls rest on a code only the parents know. Both the code lock and the Windows sign-in can be got
    /// around from the Windows lock screen (recovery, restart) when the account has no password: parental controls
    /// only on accounts with one.
    /// </summary>
    public static bool ParentalControlsAllowed(bool accountHasPassword) => accountHasPassword;
}
