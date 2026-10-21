namespace CouchHud.Core.Guard;

/// <summary>Small rules about who owns the screen.</summary>
public static class ForegroundRules
{
    /// <summary>A window of another process is in front (and not the HUD itself, and not nothing at all).</summary>
    public static bool ForeignInFront(int foregroundPid, int ownPid) => foregroundPid > 0 && foregroundPid != ownPid;
}
