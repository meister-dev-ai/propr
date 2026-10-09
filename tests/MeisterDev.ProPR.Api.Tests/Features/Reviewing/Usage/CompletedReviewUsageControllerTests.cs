using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Api.Features.Reviewing.Usage.Controllers;
using MeisterDev.ProPR.Application.Features.Reviewing.Usage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Reviewing.Usage;

public sealed class CompletedReviewUsageControllerTests
{
    [Fact]
    public async Task RequiresMachineAuthorizationAndPassesTenantScopeToExport()
    {
        var export = Substitute.For<ICompletedReviewUsageExport>();
        var controller = new CompletedReviewUsageController(export)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var clientId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        Assert.IsType<ForbidResult>(await controller.GetCompletedUsage(clientId, null));
        await export.DidNotReceiveWithAnyArgs().GetPageAsync(default, default, default, default, default);

        controller.HttpContext.Items[TenantMachineOperationPolicy.AuthorizedItemKey] = true;
        controller.HttpContext.Items[TenantMachineOperationPolicy.TenantItemKey] = tenantId;
        Assert.IsType<BadRequestResult>(await controller.GetCompletedUsage(clientId, -1));
        var page = new CompletedReviewUsagePage([], null);
        export.GetPageAsync(tenantId, clientId, 4, 25, Arg.Any<CancellationToken>()).Returns(page);
        var result = Assert.IsType<OkObjectResult>(await controller.GetCompletedUsage(clientId, 4, 25));
        Assert.Same(page, result.Value);
    }
}
