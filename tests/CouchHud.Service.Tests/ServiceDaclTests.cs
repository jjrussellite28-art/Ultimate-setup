using CouchHud.Service.Authorization;

namespace CouchHud.Service.Tests;

/// <summary>Review 3, item 4: the service's own access list decides whether a user-defined control can be trusted.</summary>
public class ServiceDaclTests
{
    [Fact]
    public void The_hardened_list_is_trusted()
    {
        Assert.Empty(ServiceDacl.UntrustedControllers(ServiceHardening.ServiceSddl)!);
    }

    [Theory]
    [InlineData("D:(A;;CCLCRPCRRC;;;IU)", "IU")]
    [InlineData("D:(A;;GA;;;WD)", "WD")]
    [InlineData("D:(A;;GX;;;AU)", "AU")]
    [InlineData("D:(A;;0x100;;;BU)", "BU")]
    [InlineData("D:(A;;0x20000000;;;SU)", "SU")]
    [InlineData("D:(A;;CR;;;S-1-5-4)", "S-1-5-4")]
    [InlineData("D:(A;;CR;;;S-1-5-21-1111111111-2222222222-3333333333-1001)", "S-1-5-21-1111111111-2222222222-3333333333-1001")]
    [InlineData("D:(A;;WD;;;IU)", "IU")]
    [InlineData("D:(A;;WO;;;BG)", "BG")]
    [InlineData("D:(A;;0x40000;;;LS)", "LS")]
    [InlineData("D:P(XA;;CR;;;IU;(Member_of {SID(BA)}))", "IU")]
    [InlineData("D:(OA;;CR;;;IU)", "IU")]
    [InlineData("D:(ZA;;GX;;;AU)", "AU")]
    [InlineData("D:(A;;FA;;;IU)", "IU")]
    [InlineData("D:(A;;KA;;;IU)", "IU")]
    [InlineData("D:(A;;FW;;;IU)", "IU")]
    [InlineData("D:(A;;CCDCLCRPRC;;;IU)", "IU")]
    [InlineData("D:(A;;GW;;;IU)", "IU")]
    [InlineData("D:(A;;KW;;;IU)", "IU")]
    [InlineData("D:(A;;CCZZRC;;;IU)", "IU")]
    [InlineData("D:(A;;CCL;;;IU)", "IU")]
    [InlineData("D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRPCR;;;IU)", "IU")]
    public void Anyone_but_administrators_and_system_with_cr_is_untrusted(string sddl, string who)
    {
        Assert.Contains(who, ServiceDacl.UntrustedControllers(sddl)!);
    }

    [Theory]
    [InlineData("D:(A;;CCLCRPRC;;;IU)")]
    [InlineData("D:(D;;CR;;;IU)")]
    [InlineData("D:(A;;CR;;;BA)(A;;GA;;;SY)")]
    [InlineData("D:(A;;0x20094;;;IU)")]
    [InlineData("D:(A;;FRFX;;;IU)")]
    [InlineData("D:(A;;KRGR;;;IU)")]
    [InlineData("D:(A;;CCLCSWRPWPDTLORCSD;;;IU)")]
    public void Lists_without_cr_for_users_are_trusted(string sddl)
    {
        Assert.Empty(ServiceDacl.UntrustedControllers(sddl)!);
    }

    [Theory]
    [InlineData("D:NO_ACCESS_CONTROL")]
    [InlineData("D:PNO_ACCESS_CONTROL")]
    public void No_access_list_at_all_is_untrusted(string sddl) => Assert.Equal(["NO_ACCESS_CONTROL"], ServiceDacl.UntrustedControllers(sddl));

    [Fact]
    public void System_and_administrators_may_hold_everything()
    {
        Assert.Empty(ServiceDacl.UntrustedControllers("D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCRPRC;;;IU)(A;;WDWOCR;;;S-1-5-32-544)(A;;CR;;;S-1-5-18)")!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("O:SY")]
    public void An_unreadable_list_is_no_answer(string? sddl) => Assert.Null(ServiceDacl.UntrustedControllers(sddl));
}
