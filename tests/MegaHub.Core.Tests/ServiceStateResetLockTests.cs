using MegaHub.Core.Config;
using MegaHub.Core.Guard;

namespace MegaHub.Core.Tests;

/// <summary>The settings' "Reset service state" (Hud.ResetServiceStateAsync): behind the couch lock, unlike the repair and the code store reset.</summary>
public class ServiceStateResetLockTests
{
    [Fact]
    public void The_reset_is_refused_on_a_locked_couch()
    {
        Assert.DoesNotContain("resetServiceState", LockPolicy.OpenWhileLocked);
        // The way out of a broken service stays open while locked; the reset of the state is not that way out.
        Assert.Contains("repairService", LockPolicy.OpenWhileLocked);
    }

    [Fact]
    public void The_reset_belongs_to_no_feature_area()
    {
        Assert.Contains("resetServiceState", FeatureAreas.AlwaysMethods);
    }
}
