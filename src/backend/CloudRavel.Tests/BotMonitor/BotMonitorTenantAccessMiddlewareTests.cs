using CloudRavel.Api.Middleware;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorTenantAccessMiddlewareTests
{
    [Fact]
    public async Task Member_with_foreign_X_Tenant_Id_is_rejected()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var userId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(userId).Returns(new User
        {
            UserId = userId,
            DisplayName = "Member",
            Email = "member@example.test",
            GlobalRole = "member",
            IsActive = true,
            AuthProvider = "local",
        });
        users.GetTenantRoleAsync(userId, tenantB).Returns((string?)null);

        var middleware = new TenantContextMiddleware(users, NullLogger<TenantContextMiddleware>.Instance);
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = "member";
        ctx.SetUser(userId);
        ctx.SetRequest(new FakeHttpRequestData(
            ctx,
            "GET",
            tenantIdHeaders: new[] { tenantB.ToString() },
            url: "http://localhost:7071/api/bot-monitor/portal/bots"));

        var nextCalled = false;
        await middleware.Invoke(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var response = ctx.GetInvocationResult() as HttpResponseData;
        Assert.NotNull(response);
        Assert.Equal(403, (int)response!.StatusCode);
        Assert.False(nextCalled);
        await users.Received(1).GetTenantRoleAsync(userId, tenantB);
        await users.DidNotReceive().GetTenantRoleAsync(userId, tenantA);
    }
}
