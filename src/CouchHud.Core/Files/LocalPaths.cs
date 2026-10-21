using System.Text.RegularExpressions;

namespace CouchHud.Core.Files;

/// <summary>
/// Which paths the virtual host local.couchhud may open. A skin or a cover can name any path, so only plain local drive
/// paths pass: no network shares (Windows would send the account's credentials to the server), no device or
/// NT namespace prefixes (\\?\ and \??\ reach shares as well), no alternate data streams, nothing that changes when normalized.
/// </summary>
public static partial class LocalPaths
{
    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    /// <summary>Is this a path on a local drive that is already in its normal form? <paramref name="isNetworkDrive"/> tells for a drive letter whether it is mapped to a share.</summary>
    public static bool IsAllowed(string? path, Func<char, bool>? isNetworkDrive = null)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024) return false;
        if (!DrivePath().IsMatch(path)) return false;
        // After "C:" there is no further colon (stream names) and none of the characters Windows reserves.
        if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(['?', '*', '"', '<', '>', '|', '\0']) >= 0) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        // "..", ".", doubled separators, trailing dots and the like change the path when it is normalized: not accepted.
        if (!string.Equals(full, path, StringComparison.OrdinalIgnoreCase)) return false;
        return isNetworkDrive is null || !isNetworkDrive(char.ToUpperInvariant(path[0]));
    }

    /// <summary>The check for the real machine: asks Windows whether the drive is a network drive.</summary>
    public static bool IsNetworkDrive(char letter)
    {
        try
        {
            return new DriveInfo(letter.ToString()).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A drive that cannot be asked about is not one to read from.
            return true;
        }
    }
}
