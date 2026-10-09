using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Application.Features.Reviewing.Usage;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.Reviewing.Usage.Controllers;

/// <summary>Provides finalized review usage to tenant machine callers.</summary>
[ApiController]
[Route("clients/{clientId:guid}/reviewing/completed-usage")]
public sealed class CompletedReviewUsageController(ICompletedReviewUsageExport export) : ControllerBase
{
    /// <summary>Returns a keyset page of finalized usage facts for a tenant-owned client.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="after">Last processed sequence, omitted for the first page.</param>
    /// <param name="limit">Maximum facts, from 1 to 100.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">A page of immutable completed-review usage facts.</response>
    /// <response code="400">Invalid cursor or page size.</response>
    /// <response code="401">Missing or invalid machine credential.</response>
    /// <response code="403">Caller is not a tenant machine or does not own the client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(CompletedReviewUsagePage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCompletedUsage(Guid clientId, [FromQuery] long? after, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (this.HttpContext.Items[TenantMachineOperationPolicy.AuthorizedItemKey] is not true ||
            this.HttpContext.Items[TenantMachineOperationPolicy.TenantItemKey] is not Guid tenantId)
        {
            return this.Forbid();
        }

        if (limit is < 1 or > 100 || after < 0)
        {
            return this.BadRequest();
        }

        return this.Ok(await export.GetPageAsync(tenantId, clientId, after, limit, ct));
    }
}
