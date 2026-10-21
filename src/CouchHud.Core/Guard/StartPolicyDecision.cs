namespace CouchHud.Core.Guard;

/// <summary>What the start does with the lock's Windows policies that an earlier run left behind.</summary>
public static class StartPolicyDecision
{
    /// <summary>
    /// A start that locks (locked, lock on, not PC mode, not a dev run) keeps what is there: the apply takes over the values, and
    /// releasing first would only produce "released", then "set" at every start. A hold for the startup programs
    /// (<c>hold</c>) only delays setting values anew, it never turns a locked start into a release. Every other start gives back
    /// what a crashed run left behind.
    /// </summary>
    public static bool ReleaseAtStart(bool locked, bool dev, bool lockOn, bool pcMode, bool hold) =>
        !(locked && !dev && lockOn && !pcMode);
}
