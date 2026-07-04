// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Admin endpoint for dismissing a review finding into thread memory for one client.</summary>
[ApiController]
[Route("clients/{clientId:guid}")]
public sealed partial class FindingDismissalController(ILogger<FindingDismissalController> logger) : ControllerBase
{
    private IActionResult? RequireAdmin()
    {
        return AuthHelpers.RequireAdmin(this.HttpContext);
    }

    /// <summary>
    ///     Dismisses a finding by storing it as an admin-dismissed memory record.
    ///     Future reviews will suppress similar findings via the memory reconsideration pipeline.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Dismiss request with the finding message and optional label.</param>
    /// <param name="memoryService">Service used to persist the dismissal as thread memory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Finding dismissed and memory record created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="403">Caller is not an admin.</response>
    [HttpPost("reviewing/dismiss-finding")]
    [HttpPost("dismiss-finding")]
    [ProducesResponseType(typeof(ThreadMemoryRecordDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DismissFinding(
        Guid clientId,
        [FromBody] DismissFindingRequest request,
        [FromServices] IThreadMemoryService memoryService,
        CancellationToken ct = default)
    {
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(request.FindingMessage))
        {
            return this.BadRequest(new { error = "findingMessage is required." });
        }

        var record = await memoryService.DismissFindingAsync(
            clientId,
            request.FilePath,
            request.FindingMessage,
            request.Label,
            ct);

        LogDismissalCreated(logger, record.Id, clientId);

        return this.StatusCode(201, ToDto(record));
    }

    private static ThreadMemoryRecordDto ToDto(ThreadMemoryRecord r)
    {
        return new ThreadMemoryRecordDto(
            r.Id,
            r.ClientId,
            r.ThreadId,
            r.RepositoryId,
            r.PullRequestId,
            r.FilePath,
            r.ResolutionSummary,
            r.CreatedAt,
            r.UpdatedAt);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Finding dismissal memory record {Id} created for client {ClientId}")]
    private static partial void LogDismissalCreated(ILogger logger, Guid id, Guid clientId);
}

/// <summary>Request to dismiss a finding and store it as an admin-dismissed memory record.</summary>
/// <param name="FindingMessage">The original finding message to dismiss.</param>
/// <param name="FilePath">Optional file path to scope the dismissal.</param>
/// <param name="Label">Optional human-readable label for the dismissal.</param>
public sealed record DismissFindingRequest(string FindingMessage, string? FilePath = null, string? Label = null);
