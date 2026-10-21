namespace CouchHud.Core.Startup;

/// <summary>
/// Exit codes of the elevated setup helper and the message each one gets. The helper runs after the Windows
/// prompt was confirmed, so a code other than 0 says what went wrong afterwards, never "prompt declined"
/// (that one is the Win32 error 1223 when the process cannot even be started).
/// </summary>
public static class SetupExit
{
    public const int Ok = 0;
    /// <summary>The arguments did not pass the helper's own check.</summary>
    public const int BadInput = 2;
    /// <summary>CouchHUD is not in a folder only administrators can write to.</summary>
    public const int UnsafeFolder = 3;
    /// <summary>A scheduled task or file could not be created.</summary>
    public const int TaskFailed = 4;
    /// <summary>The lock's Windows policies could not be released (checked in the registry): no helper task was deleted.</summary>
    public const int PoliciesStay = 5;

    /// <summary>The string key of the message for a code, with {0} = the code where the text has one.</summary>
    public static string MessageKey(int code) => code switch
    {
        BadInput => "services.setupBadInput",
        UnsafeFolder => "services.needProtected",
        TaskFailed => "services.setupTaskFailed",
        PoliciesStay => "services.setupPoliciesStay",
        _ => "services.setupFailed"
    };
}
