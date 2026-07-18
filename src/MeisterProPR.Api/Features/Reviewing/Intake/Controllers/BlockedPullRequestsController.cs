// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Reviewing.Intake.Controllers;

/// <summary>
///     Manages the set of pull requests blocked from review processing for a client. A block prevents new
///     review jobs from being created on future pushes or submissions; it does not stop a job that is
///     already running (use the stop action for that). Reading the list requires
///     <see cref="ClientRole.ClientUser" />; blocking and unblocking require
///     <see cref="ClientRole.ClientAdministrator" />.
/// </summary>
[ApiController]
public sealed partial class BlockedPullRequestsController(
    IBlockedPullRequestStore blockedPullRequestStore,
    ILogger<BlockedPullRequestsController> logger) : ControllerBase
{
    /// <summary>List the pull requests currently blocked from review processing for a client.</summary>
    /// <param name="clientId">ID of the client whose blocked pull requests are listed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The blocked pull requests for the client.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    [HttpGet("/clients/{clientId:guid}/reviewing/blocked-prs")]
    [ProducesResponseType(typeof(IReadOnlyList<BlockedPullRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetBlockedPullRequests(Guid clientId, CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        var blocks = await blockedPullRequestStore.ListForClientAsync(clientId, ct);
        var response = blocks
            .Select(b => new BlockedPullRequestDto(
                b.Id,
                b.ClientId,
                b.ProviderScopePath,
                b.ProviderProjectKey,
                b.RepositoryId,
                b.PullRequestId,
                b.BlockedByUserId,
                b.BlockedAt,
                b.Reason))
            .ToArray();

        return this.Ok(response);
    }

    /// <summary>Block a pull request from review processing.</summary>
    /// <param name="clientId">ID of the client that owns the pull request.</param>
    /// <param name="request">The pull request to block, and an optional reason.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The pull request is blocked (already blocked requests are accepted idempotently).</response>
    /// <response code="400">The request is missing required pull-request identity fields.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks <c>ClientAdministrator</c> rights for the client.</response>
    [HttpPost("/clients/{clientId:guid}/reviewing/blocked-prs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BlockPullRequest(
        Guid clientId,
        [FromBody] BlockPullRequestRequest request,
        CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (!TryValidateIdentity(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                out var validationError))
        {
            return this.BadRequest(new { error = validationError });
        }

        var userId = AuthHelpers.GetUserId(this.HttpContext);
        if (userId is null || userId.Value == Guid.Empty)
        {
            return this.Unauthorized();
        }

        var created = await blockedPullRequestStore.BlockAsync(
            clientId,
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            userId.Value,
            request.Reason,
            ct);

        if (created)
        {
            LogPullRequestBlocked(logger, clientId, request.PullRequestId);
        }

        return this.Ok();
    }

    /// <summary>Unblock a pull request so future pushes are processed again.</summary>
    /// <param name="clientId">ID of the client that owns the pull request.</param>
    /// <param name="request">The pull request to unblock.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The pull request is not blocked (already-unblocked requests are accepted idempotently).</response>
    /// <response code="400">The request is missing required pull-request identity fields.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks <c>ClientAdministrator</c> rights for the client.</response>
    [HttpPost("/clients/{clientId:guid}/reviewing/blocked-prs/unblock")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UnblockPullRequest(
        Guid clientId,
        [FromBody] UnblockPullRequestRequest request,
        CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (!TryValidateIdentity(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                out var validationError))
        {
            return this.BadRequest(new { error = validationError });
        }

        var removed = await blockedPullRequestStore.UnblockAsync(
            clientId,
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            ct);

        if (removed)
        {
            LogPullRequestUnblocked(logger, clientId, request.PullRequestId);
        }

        return this.Ok();
    }

    private static bool TryValidateIdentity(
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        out string? validationError)
    {
        validationError = null;

        if (string.IsNullOrWhiteSpace(providerScopePath))
        {
            validationError = "ProviderScopePath is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(providerProjectKey))
        {
            validationError = "ProviderProjectKey is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            validationError = "RepositoryId is required.";
            return false;
        }

        if (pullRequestId < 1)
        {
            validationError = "PullRequestId must be greater than zero.";
            return false;
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Blocked PR #{PrId} from review processing for client {ClientId}")]
    private static partial void LogPullRequestBlocked(ILogger logger, Guid clientId, int prId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unblocked PR #{PrId} for client {ClientId}")]
    private static partial void LogPullRequestUnblocked(ILogger logger, Guid clientId, int prId);
}
