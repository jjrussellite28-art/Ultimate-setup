using MegaHub.Core.Plugins;

namespace MegaHub.Core.Tests;

/// <summary>Checks for an entry of the repository's catalog that carries Mega Hub's own signed package statement.</summary>
internal static class SignedCatalogEntry
{
    /// <summary>
    /// The statement verifies first-party against the key compiled into Mega Hub, names exactly this package (id, folder, version, hash)
    /// and an icon, so the catalog can install the plugin and show its icon.
    /// </summary>
    /// <summary>
    /// An entry the owner has not signed yet (a new version or plugin prepared before the signing run, no statement and no hash) is listed
    /// but cannot be installed; once the statement is in, it must be the owner's own for exactly this package.
    /// </summary>
    public static void AssertOwnSignedOrWaiting(CatalogEntry entry)
    {
        if (entry.Statement is null)
        {
            Assert.Null(entry.Signature);
            Assert.NotNull(entry.Problem);
            return;
        }
        AssertOwnSigned(entry);
    }

    public static PackageStatement AssertOwnSigned(CatalogEntry entry)
    {
        Assert.NotNull(entry.Statement);
        Assert.NotNull(entry.Signature);
        Assert.Matches("^[0-9a-f]{64}$", entry.Sha256!);
        Assert.Null(entry.Problem);
        var verified = PackageStatements.Verify(new SignedStatement(Convert.FromBase64String(entry.Statement!), entry.Signature, null));
        Assert.Equal(PluginOrigin.FirstParty, verified.Origin);
        PackageStatements.CheckMatches(verified, entry.Sha256!, entry);
        Assert.Equal(entry.Id, verified.Statement.Id);
        if (entry.IconUrl is not null) Assert.NotNull(verified.Statement.Icon);
        return verified.Statement;
    }
}
