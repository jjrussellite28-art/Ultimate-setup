namespace CouchHud.Core.Guard;

/// <summary>
/// The Xbox button belongs to CouchHud on the couch: the Game Bar's claim on it is switched off for
/// as long as CouchHud is in charge (through <see cref="GuardSnapshot"/>, so the original comes back).
/// Capture settings (AppCaptureEnabled, GameDVR_Enabled) are deliberately not touched, except on a locked couch,
/// where <see cref="CaptureOff"/> also takes the keyboard shortcut (Win+G) away.
/// </summary>
public static class GameBarGuard
{
    public const string Key = @"HKCU\Software\Microsoft\GameBar";
    public const string Value = "UseNexusForGameBarEnabled";

    public static readonly GuardChange Change = new(GuardKind.RegistryDword, Key, Value, "0");

    public const string CaptureKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR";

    /// <summary>Locked couch only (part of <see cref="LockHardening"/>): the Game Bar shortcut does nothing, captures through it stop.</summary>
    public static readonly GuardChange CaptureOff = new(GuardKind.RegistryDword, CaptureKey, "AppCaptureEnabled", "0");

    /// <summary>
    /// Older versions set the value to 0 without a backup. Once, on upgrade: if there is no snapshot, the value is 0 and
    /// the config shows a version that did it (used before, guide tap on), the original was "missing".
    /// </summary>
    public static bool NeedsMigration(bool alreadyMigrated, bool snapshotPending, bool valueIsZero, bool usedBefore, bool guideTapInGame) =>
        !alreadyMigrated && !snapshotPending && valueIsZero && usedBefore && guideTapInGame;

    /// <summary>Only in full integration on the couch, with the option on and the guide tap in use; a locked couch takes the button whatever the options say.</summary>
    public static bool ShouldApply(bool full, bool pcMode, bool suppressCompetitors, bool guideTapInGame, bool locked = false) =>
        full && !pcMode && (locked || suppressCompetitors && guideTapInGame);
}
