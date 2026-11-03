using CouchHud.Core.Remote;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using Xunit;

namespace CouchHud.Service.Tests;

/// <summary>
/// The second factor (0.7) lives in the HUD (see SECURITY.md), but its list must know every operation of the service that takes a grant: a new
/// operation with a code or an approval cannot slip in without being classified (needs the second factor, or left out with a reason).
/// </summary>
public class SecondFactorCoverageTests
{
    [Fact]
    public void Every_operation_that_takes_a_grant_is_classified()
    {
        var takesGrant = OperationPolicy.Table
            .Where(kv => kv.Value.Requirement is Requirement.GrantOrAdmin or Requirement.ParentGrantOrAdmin or Requirement.GrantOrAdminOnceCodeEnrolled or Requirement.Admin)
            .Select(kv => kv.Key.ToString())
            .Concat([nameof(Operation.EnrollPasscode), nameof(Operation.SetLockMode)])
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        var missing = takesGrant.Where(n => !SecondFactorActions.ServiceOperations.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0, "operations without a second-factor classification: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_classification_names_only_operations_that_exist()
    {
        var all = Enum.GetNames<Operation>().ToHashSet();
        Assert.All(SecondFactorActions.ServiceOperations.Keys, k => Assert.Contains(k, all));
    }
}
