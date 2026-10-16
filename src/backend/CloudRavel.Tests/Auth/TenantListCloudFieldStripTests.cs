using System.Net;
using System.Text.Json;
using CloudRavel.Api.Functions;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using CloudRavel.Tests.Assessment;
using CloudRavel.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class TenantListCloudFieldStripTests
{
    [Fact]
    public async Task Bot_only_member_gets_tenant_list_without_cloud_stats()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetByUserAccessAsync(userId).Returns(new List<Tenant>
        {
            new()
            {
                TenantId = tenantId,
                DisplayName = "Acme",
                AzureTenantId = "00000000-0000-0000-0000-000000000001",
                Status = TenantStatus.Active,
            },
        });

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetFeatureEntitlementsAsync(userId, tenantId)
            .Returns(new FeatureEntitlements(BotMonitoring: true, CloudMonitoring: false));

        var inventory = Substitute.For<IInventoryRepository>();
        var fn = new TenantFunctions(
            tenantRepo,
            userRepo,
            inventory,
            Substitute.For<IChangeRepository>(),
            Substitute.For<IRecommendationRepository>(),
            Substitute.For<IAuditRepository>(),
            Substitute.For<IInventoryCollectionService>(),
            NullLogger<TenantFunctions>.Instance);

        var ctx = new Assessment.TestFunctionContext();
        ctx.Items["SystemRole"] = "member";
        ctx.SetUser(userId);
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: null, url: "http://localhost:7071/api/tenants");
        ctx.SetRequest(req);

        var res = await fn.ListTenants(req, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = res.ReadBody();
        var body = JsonSerializer.Deserialize<TenantListResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Single(body!.Tenants);
        Assert.Null(body.Tenants[0].ResourceCount);
        Assert.Null(body.Tenants[0].LastSnapshotAt);
        Assert.Null(body.Tenants[0].Changes24H);
        await inventory.DidNotReceive().GetResourceCountAsync(Arg.Any<Guid>());
    }

    private sealed class TenantListResponseDto
    {
        public List<TenantSummaryDto> Tenants { get; set; } = [];
    }

    private sealed class TenantSummaryDto
    {
        public int? ResourceCount { get; set; }
        public DateTime? LastSnapshotAt { get; set; }
        public int? Changes24H { get; set; }
    }
}
