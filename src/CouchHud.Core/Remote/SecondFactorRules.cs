namespace CouchHud.Core.Remote;

/// <summary>Why the second factor cannot be switched on.</summary>
public enum SecondFactorBlock
{
    None,
    /// <summary>The couch lock is off: there is no first factor to add to.</summary>
    NoCodeCheck,
    /// <summary>No phone with the full right that is not a child's: nobody could confirm.</summary>
    NoPhone
}

/// <summary>What to warn about when the couch code is switched off.</summary>
public enum CodeOffWarning
{
    /// <summary>The code check is off already (or stays on).</summary>
    None,
    /// <summary>Everybody gets into the couch.</summary>
    Plain,
    /// <summary>The same, and the second factor goes with it.</summary>
    WithSecondFactor
}

/// <summary>The pure rules around the second factor (ab 0.7); the host and the tests use the same ones.</summary>
public static class SecondFactorRules
{
    /// <summary>
    /// May this phone confirm right now: sealed channel (a key), not a child's phone, and the full right as it is in effect (a child's profile
    /// on the couch or PC mode lower it, <see cref="RemoteRights.Effective"/>). The same rule for asking, answering and switching on.
    /// </summary>
    public static bool MayConfirm(RemoteDevice device, string activeProfile, bool pcMode) =>
        device.Key.Length > 0 && !device.Child && RemoteRights.Effective(device, activeProfile, pcMode) >= RemoteLevel.Full;

    /// <summary>A phone that could confirm when asked: stored right is Full, not a child's, has a key (independent of the current profile or PC mode).</summary>
    public static bool IsConfirmer(RemoteDevice device) =>
        device.Key.Length > 0 && !device.Child && RemoteRights.Parse(device.Level) == RemoteLevel.Full;

    /// <summary>Whether it can be switched on: needs the code check and one phone that can confirm.</summary>
    public static SecondFactorBlock CanEnable(string couchLock, IEnumerable<RemoteDevice> devices) =>
        couchLock == "off" ? SecondFactorBlock.NoCodeCheck
        : devices.Any(IsConfirmer) ? SecondFactorBlock.None
        : SecondFactorBlock.NoPhone;

    /// <summary>Whether it stays on after a change of the lock or the phones (it ends with the code check; fail-open for nothing else).</summary>
    public static bool StillPossible(string couchLock, IEnumerable<RemoteDevice> devices) => CanEnable(couchLock, devices) == SecondFactorBlock.None;

    /// <summary>Which warning the dialog for switching the code check off shows.</summary>
    public static CodeOffWarning WarnOnCodeOff(string currentLock, string newLock, bool secondFactorOn) =>
        currentLock != "off" && newLock == "off" ? (secondFactorOn ? CodeOffWarning.WithSecondFactor : CodeOffWarning.Plain) : CodeOffWarning.None;

    /// <summary>
    /// The code the guard waits for, as the site asks it with a parents' code: the grant fits when a parents' code exists, else the couch must be
    /// unlocked. (A site that goes through the parents' gate; see <see cref="SiteCodeAccepted"/> for one that asks the grant only with parental controls.)
    /// </summary>
    public static bool CodeAccepted(bool hasParentCode, bool grantFits, bool unlocked) => hasParentCode ? grantFits : unlocked;

    /// <summary>
    /// The same for a site that asks the parents' grant only with parental controls on and otherwise the unlocked couch. Exactly what the site
    /// demands, no stricter: with a parents' code held but parental controls off the unlocked couch is all the site wants, so the guard must
    /// ask the phone then (a stricter reading would skip it where the site lets the action through).
    /// </summary>
    public static bool SiteCodeAccepted(bool parentalEnabled, bool hasParentCode, bool grantFits, bool unlocked) =>
        parentalEnabled ? CodeAccepted(hasParentCode, grantFits, unlocked) : unlocked;

    /// <summary>The action needs a confirmation now: the second factor is on and the action is on the list.</summary>
    public static bool Required(bool secondFactorOn, string action) => secondFactorOn && SecondFactorActions.IsCritical(action);

    /// <summary>
    /// The token's elevation type (TokenElevationType: 1 default, 2 full, 3 limited). An administrator account in Admin Approval Mode has the
    /// limited type; for it UAC is only a yes/no click, so the emergency way through an administrator is no real second proof there.
    /// </summary>
    public static bool IsAdminAccount(int elevationType) => elevationType is 2 or 3;

    public const int MaxDetail = 60;

    /// <summary>The line the phone and the TV show under the action (a profile, a rule, a plugin): no control characters, cut to a short length.</summary>
    public static string CleanDetail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = new string(text.Where(c => !char.IsControl(c) && c != (char)0x2028 && c != (char)0x2029).ToArray()).Trim();
        return clean.Length > MaxDetail ? clean[..MaxDetail] + "…" : clean;
    }

    /// <summary>A new level is higher than the old (only then the phone's right needs the second factor).</summary>
    public static bool Raises(string? oldLevel, string newLevel) => RemoteRights.Parse(newLevel) > RemoteRights.Parse(oldLevel);
}
