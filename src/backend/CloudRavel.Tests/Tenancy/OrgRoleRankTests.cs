using CloudRavel.Api.Middleware;
using Xunit;

namespace CloudRavel.Tests.Tenancy;

/// <summary>
/// Documents the org-role hierarchy used by RequireOrgRoleAsync.
/// Full HTTP isolation tests need a host; these lock the ranking constants.
/// </summary>
public sealed class OrgRoleRankTests
{
    [Fact]
    public void Role_constants_are_stable()
    {
        Assert.Equal("system_admin", SystemRole.SystemAdmin);
        Assert.Equal("member", SystemRole.Member);
        Assert.Equal("org_admin", OrgRole.OrgAdmin);
        Assert.Equal("cloud_admin", OrgRole.CloudAdmin);
        Assert.Equal("read_only", OrgRole.ReadOnly);
    }
}
