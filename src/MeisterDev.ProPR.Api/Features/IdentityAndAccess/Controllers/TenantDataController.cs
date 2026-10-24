// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MeisterDev.ProPR.Application.Features.TenantData;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>The protocol retention of a tenant.</summary>
/// <param name="Days">How many days protocol events are kept, from 1 to 3650, or null to keep them.</param>
public sealed record TenantProtocolRetentionRequest([Range(1, 3650)] int? Days);

/// <summary>An instruction to erase one developer's identity from the review records of a tenant.</summary>
/// <param name="ExternalUserId">The provider's identifier of the developer's account.</param>
/// <param name="Login">The login of the account, which comments may name instead of the identifier.</param>
/// <param name="Instruction">The reference of the controller's instruction, which the tenant audit records.</param>
public sealed record AuthorErasureRequest(
    [Required, MaxLength(256)] string ExternalUserId,
    [MaxLength(256)] string? Login,
    [Required, MaxLength(120)] string Instruction);

/// <summary>The answer while a tenant deletion waits for the work of the tenant to end.</summary>
/// <param name="Error">Always <c>tenant_work_running</c>.</param>
/// <param name="Message">What the caller does next.</param>
/// <param name="RetryAfterSeconds">The seconds after which the caller requests the deletion again, as in the Retry-After header.</param>
/// <param name="Running">The executions that may still be running, by kind.</param>
public sealed record TenantDeletionWaitResponse(
    string Error,
    string Message,
    int RetryAfterSeconds,
    IReadOnlyDictionary<string, int> Running);

/// <summary>Deletes, exports and retains the data of a tenant. Platform administrators only.</summary>
[ApiController]
[Route("admin/tenants/{tenantId:guid}")]
public sealed class TenantDataController(ITenantDataLifecycle lifecycle) : ControllerBase
{
    /// <summary>
    ///     Stops the work of the tenant and deletes the tenant once that work has ended. Each request deactivates the
    ///     tenant, its clients and their webhook, crawl, mention and ProCursor configurations and ends their unfinished
    ///     jobs. While an execution may still be running, or for a minute after a request stopped any work, the request
    ///     answers 409 with the seconds to wait. A request after that deletes the tenant, its clients and every record
    ///     that belongs to them, including review jobs, protocols, retained discussions, usage reports and the ProCursor
    ///     indexes. Accounts that belong to this tenant alone are deleted with it.
    /// </summary>
    /// <response code="200">The tenant was deleted; the answer lists the rows deleted per table.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist.</response>
    /// <response code="409">
    ///     The work of the tenant was stopped and the deletion waits for it; the Retry-After header gives the seconds to
    ///     wait. A problem answer means that the tenant is the system tenant.
    /// </response>
    [HttpDelete]
    [ProducesResponseType(typeof(TenantDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(TenantDeletionWaitResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteTenant(Guid tenantId, CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        TenantDeletionAnswer? answer;
        try
        {
            answer = await lifecycle.DeleteTenantAsync(tenantId, ct);
        }
        catch (TenantDataLifecycleException exception)
        {
            return this.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }

        if (answer is null)
        {
            return this.NotFound();
        }

        if (answer.Pending is not { } pending)
        {
            return this.Ok(answer.Deleted);
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(pending.RetryAfter.TotalSeconds));
        this.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return this.Conflict(
            new TenantDeletionWaitResponse(
                "tenant_work_running",
                $"The work of the tenant was stopped. Request the deletion again after {seconds} seconds.",
                seconds,
                pending.Running));
    }

    /// <summary>
    ///     Exports the tenant as one JSON document in the format <c>propr.tenant-export</c>, version 1: the tenant, its
    ///     clients with their SCM connections and scopes without secrets, thread memory, and review jobs with their
    ///     file results. Protocols and their events are included on request.
    /// </summary>
    /// <response code="200">The export.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist.</response>
    [HttpGet("export")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task ExportTenant(Guid tenantId, [FromQuery] bool includeProtocols, CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            await refused.ExecuteResultAsync(this.ControllerContext);
            return;
        }

        // The status of a response is final once its first byte is sent. The export is therefore written to a
        // temporary file first, so a missing tenant still receives 404 and a failed export does not end in a
        // truncated document with status 200.
        await using var buffer = new FileStream(
            Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 64 * 1024, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        if (!await lifecycle.WriteExportAsync(tenantId, buffer, new(includeProtocols), ct))
        {
            this.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        this.Response.ContentType = "application/json";
        this.Response.Headers.ContentDisposition = $"attachment; filename=\"tenant-{tenantId:N}.json\"";
        buffer.Position = 0;
        await buffer.CopyToAsync(this.Response.Body, ct);
    }

    /// <summary>Sets how long the protocol events of the tenant are kept.</summary>
    /// <response code="204">The retention was saved.</response>
    /// <response code="400">The retention is outside 1 to 3650 days.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist.</response>
    [HttpPut("protocol-retention")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetProtocolRetention(
        Guid tenantId,
        [FromBody] TenantProtocolRetentionRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        return await lifecycle.SetProtocolRetentionAsync(tenantId, request.Days, ct)
            ? this.NoContent()
            : this.NotFound();
    }

    /// <summary>
    ///     Erases one developer's identity from the review records of the tenant on the instruction of the customer,
    ///     who is the controller of that data. The tenant audit records the erasure.
    /// </summary>
    /// <response code="200">The identity was erased; the answer counts the changed records.</response>
    /// <response code="400">The instruction is missing or too long.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist.</response>
    [HttpPost("identity-erasures")]
    [ProducesResponseType(typeof(IdentityErasureResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EraseAuthor(
        Guid tenantId, [FromBody] AuthorErasureRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        var result = await lifecycle.EraseAuthorAsync(
            tenantId, new(request.ExternalUserId, request.Login),
            AuthHelpers.GetUserId(this.HttpContext), request.Instruction, ct);
        return result is null ? this.NotFound() : this.Ok(result);
    }
}
