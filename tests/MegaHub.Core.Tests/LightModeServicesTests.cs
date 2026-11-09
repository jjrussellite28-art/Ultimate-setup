using MegaHub.Core.Config;

namespace MegaHub.Core.Tests;

/// <summary>LightModeServices: the change to light mode resumes the applied services, not the selection.</summary>
public class LightModeServicesTests
{
    [Fact]
    public void An_emptied_selection_still_resumes_the_applied_services()
    {
        var config = new AppConfig { CouchServices = [], CouchServicesInstalled = ["WSearch", "SysMain"] };
        Assert.Equal(["WSearch", "SysMain"], LightModeServices.ToResume(config));
    }

    [Fact]
    public void A_selection_not_applied_yet_resumes_nothing()
    {
        var config = new AppConfig { CouchServices = ["WSearch"], CouchServicesInstalled = [] };
        Assert.Empty(LightModeServices.ToResume(config));
    }

    [Fact]
    public void A_changed_selection_resumes_what_was_applied()
    {
        var config = new AppConfig { CouchServices = ["SysMain"], CouchServicesInstalled = ["WSearch"] };
        Assert.Equal(["WSearch"], LightModeServices.ToResume(config));
    }

    [Fact]
    public void The_list_is_a_copy()
    {
        var config = new AppConfig { CouchServicesInstalled = ["WSearch"] };
        var resume = LightModeServices.ToResume(config);
        config.CouchServicesInstalled.Clear();
        Assert.Equal(["WSearch"], resume);
    }
}
