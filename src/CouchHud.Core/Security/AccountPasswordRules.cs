namespace CouchHud.Core.Security;

/// <summary>
/// Whether a Windows account has a password, decided without a failed sign-in where possible. A failed LogonUser
/// counts towards the account lockout policy, so it is only the last resort.
/// </summary>
public static class AccountPasswordRules
{
    /// <summary>USER_INFO_1.usri1_flags: the account may have an empty password.</summary>
    public const uint PasswordNotRequired = 0x0020;

    /// <summary>"Blank passwords restricted to console logon" (LimitBlankPasswordUse).</summary>
    public const int ErrorAccountRestriction = 1327;

    /// <summary>
    /// From the account flags. Without UF_PASSWD_NOTREQD Windows insists on a password: true, no probe needed. With it the
    /// account may still have one (an administrator set it), so only a probe can tell: null.
    /// </summary>
    public static bool? FromFlags(uint flags) => (flags & PasswordNotRequired) == 0 ? true : null;

    /// <summary>
    /// From a LogonUser try with an empty password: it works, or the system refuses blank passwords for this account
    /// (1327): there is no usable password. Any other failure means a wrong password: there is one.
    /// </summary>
    public static bool FromBlankLogon(bool succeeded, int error) => !(succeeded || error == ErrorAccountRestriction);

    /// <summary>A probe repeats at most this often (it is a failed sign-in when the account does have a password).</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);
}
