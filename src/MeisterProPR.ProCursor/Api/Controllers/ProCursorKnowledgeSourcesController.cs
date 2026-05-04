// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.ProCursor.Api.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Client-scoped admin endpoints for managing ProCursor knowledge sources and tracked branches.
/// </summary>
[ApiController]
public sealed partial class ProCursorKnowledgeSourcesController(
    IProCursorGateway proCursorGateway,
    ILogger<ProCursorKnowledgeSourcesController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private async Task<IActionResult?> RequireProCursorCapabilityAsync(CancellationToken ct)
    {
        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.ProCursor,
            ct);

        return capability is null
            ? null
            : this.Conflict(
                new PremiumFeatureUnavailablePayload(
                    "premium_feature_unavailable",
                    capability.Key,
                    capability.Message ?? $"Capability '{capability.Key}' is unavailable."));
    }

    /// <summary>
    ///     Returns the ProCursor knowledge sources configured for the given client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Configured ProCursor sources for the client.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have access to the client.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/sources")]
    [ProducesResponseType(typeof(IReadOnlyList<ProCursorKnowledgeSourceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListSources(Guid clientId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireProCursorCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var sources = await proCursorGateway.ListSourcesAsync(clientId, ct);
            return this.Ok(sources.Select(MapSource).ToList().AsReadOnly());
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
    }

    /// <summary>
    ///     Creates a new ProCursor repository or git-backed wiki source for the given client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Knowledge source registration payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Source created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">A duplicate source already exists.</response>
    [HttpPost("/admin/clients/{clientId:guid}/procursor/sources")]
    [ProducesResponseType(typeof(ProCursorKnowledgeSourceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateSource(
        Guid clientId,
        [FromBody] ProCursorKnowledgeSourceRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateCreateRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var capability = await this.RequireProCursorCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var source = await proCursorGateway.CreateSourceAsync(
                clientId,
                new ProCursorKnowledgeSourceRegistrationRequest(
                    request.DisplayName,
                    request.SourceKind,
                    request.ProviderScopePath,
                    request.ProviderProjectKey,
                    request.RepositoryId,
                    request.DefaultBranch,
                    request.RootPath,
                    request.SymbolMode,
                    request.TrackedBranches.Select(branch => new ProCursorTrackedBranchCreateRequest(
                            branch.BranchName,
                            branch.RefreshTriggerMode,
                            branch.MiniIndexEnabled))
                        .ToList()
                        .AsReadOnly(),
                    request.OrganizationScopeId,
                    request.CanonicalSourceRef,
                    request.SourceDisplayName),
                ct);

            LogSourceCreated(logger, clientId, source.Id);
            return this.CreatedAtAction(nameof(this.ListSources), new { clientId }, MapSource(source));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Queues a durable ProCursor index job for the selected tracked branch or the source default branch.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge source identifier.</param>
    /// <param name="request">Optional refresh request overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="202">Refresh job queued.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    /// <response code="404">Client or source not found.</response>
    /// <response code="409">Embedding configuration is incomplete or incompatible for the client.</response>
    [HttpPost("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/refresh")]
    [ProducesResponseType(typeof(ProCursorRefreshResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> QueueRefresh(
        Guid clientId,
        Guid sourceId,
        [FromBody] ProCursorRefreshRequest? request = null,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireProCursorCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var job = await proCursorGateway.QueueRefreshAsync(
                clientId,
                sourceId,
                request ?? new ProCursorRefreshRequest(),
                ct);
            return this.Accepted(MapRefresh(job));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Returns the tracked branches configured for the given ProCursor source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge source identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Tracked branches returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have access to the client.</response>
    /// <response code="404">Client or source not found.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches")]
    [ProducesResponseType(typeof(IReadOnlyList<ProCursorTrackedBranchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListTrackedBranches(Guid clientId, Guid sourceId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireProCursorCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var branches = await proCursorGateway.ListTrackedBranchesAsync(clientId, sourceId, ct);
            return this.Ok(branches.Select(MapTrackedBranch).ToList().AsReadOnly());
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
    }

    /// <summary>
    ///     Adds a tracked branch to an existing ProCursor source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge source identifier.</param>
    /// <param name="request">Tracked-branch creation payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Tracked branch created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    /// <response code="404">Client or source not found.</response>
    /// <response code="409">A duplicate tracked branch already exists.</response>
    [HttpPost("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches")]
    [ProducesResponseType(typeof(ProCursorTrackedBranchResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddTrackedBranch(
        Guid clientId,
        Guid sourceId,
        [FromBody] ProCursorTrackedBranchRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (!this.ModelState.IsValid || string.IsNullOrWhiteSpace(request.BranchName))
        {
            if (string.IsNullOrWhiteSpace(request.BranchName))
            {
                this.ModelState.AddModelError(nameof(request.BranchName), "BranchName is required.");
            }

            return this.ValidationProblem();
        }

        var capability = await this.RequireProCursorCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var branch = await proCursorGateway.AddTrackedBranchAsync(
                clientId,
                sourceId,
                new ProCursorTrackedBranchCreateRequest(
                    request.BranchName,
                    request.RefreshTriggerMode,
                    request.MiniIndexEnabled),
                ct);

            return this.StatusCode(StatusCodes.Status201Created, MapTrackedBranch(branch));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Updates refresh behavior for one tracked branch.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge source identifier.</param>
    /// <param name="branchId">Tracked branch identifier.</param>
    /// <param name="request">Tracked-branch update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Tracked branch updated.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    /// <response code="404">Client, source, or branch not found.</response>
    [HttpPut("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches/{branchId:guid}")]
    [ProducesResponseType(typeof(ProCursorTrackedBranchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTrackedBranch(
        Guid clientId,
        Guid sourceId,
        Guid branchId,
        [FromBody] ProCursorTrackedBranchPatchRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireProCursorCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var branch = await proCursorGateway.UpdateTrackedBranchAsync(
                clientId,
                sourceId,
                branchId,
                new ProCursorTrackedBranchUpdateRequest(
                    request.RefreshTriggerMode,
                    request.MiniIndexEnabled,
                    request.IsEnabled),
                ct);

            return branch is null ? this.NotFound() : this.Ok(MapTrackedBranch(branch));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
    }

    /// <summary>
    ///     Removes a tracked branch from a ProCursor source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge source identifier.</param>
    /// <param name="branchId">Tracked branch identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Tracked branch removed.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    /// <response code="404">Client, source, or branch not found.</response>
    /// <response code="409">The last tracked branch cannot be removed.</response>
    [HttpDelete("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches/{branchId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveTrackedBranch(
        Guid clientId,
        Guid sourceId,
        Guid branchId,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireProCursorCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var removed = await proCursorGateway.RemoveTrackedBranchAsync(clientId, sourceId, branchId, ct);
            return removed ? this.NoContent() : this.NotFound();
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    private IActionResult? ValidateCreateRequest(ProCursorKnowledgeSourceRequest request)
    {
        if (!this.ModelState.IsValid)
        {
            return this.ValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            this.ModelState.AddModelError(nameof(request.DisplayName), "DisplayName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderScopePath))
        {
            if (string.IsNullOrWhiteSpace(request.ProviderProjectKey))
            {
                this.ModelState.AddModelError(nameof(request.ProviderProjectKey), "ProviderProjectKey is required.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.DefaultBranch))
        {
            this.ModelState.AddModelError(nameof(request.DefaultBranch), "DefaultBranch is required.");
        }

        if (request.TrackedBranches.Count == 0)
        {
            this.ModelState.AddModelError(nameof(request.TrackedBranches), "At least one tracked branch is required.");
        }

        var hasGuidedSelection = request.OrganizationScopeId.HasValue ||
                                 request.CanonicalSourceRef is not null ||
                                 !string.IsNullOrWhiteSpace(request.SourceDisplayName);

        if (hasGuidedSelection)
        {
            if (!request.OrganizationScopeId.HasValue)
            {
                this.ModelState.AddModelError(
                    nameof(request.OrganizationScopeId),
                    "OrganizationScopeId is required for guided source selection.");
            }

            if (request.CanonicalSourceRef is null)
            {
                this.ModelState.AddModelError(
                    nameof(request.CanonicalSourceRef),
                    "CanonicalSourceRef is required for guided source selection.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.CanonicalSourceRef.Provider))
                {
                    this.ModelState.AddModelError(
                        nameof(request.CanonicalSourceRef.Provider),
                        "CanonicalSourceRef.Provider is required.");
                }

                if (string.IsNullOrWhiteSpace(request.CanonicalSourceRef.Value))
                {
                    this.ModelState.AddModelError(
                        nameof(request.CanonicalSourceRef.Value),
                        "CanonicalSourceRef.Value is required.");
                }
            }

            if (string.IsNullOrWhiteSpace(request.SourceDisplayName))
            {
                this.ModelState.AddModelError(
                    nameof(request.SourceDisplayName),
                    "SourceDisplayName is required for guided source selection.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.ProviderScopePath))
            {
                this.ModelState.AddModelError(nameof(request.ProviderScopePath), "ProviderScopePath is required.");
            }

            if (string.IsNullOrWhiteSpace(request.RepositoryId))
            {
                this.ModelState.AddModelError(nameof(request.RepositoryId), "RepositoryId is required.");
            }
        }

        return this.ModelState.ErrorCount == 0 ? null : this.ValidationProblem();
    }

    private static ProCursorKnowledgeSourceResponse MapSource(ProCursorKnowledgeSourceDto source)
    {
        return new ProCursorKnowledgeSourceResponse(
            source.Id,
            source.DisplayName,
            source.SourceKind,
            source.ProviderScopePath,
            source.ProviderProjectKey,
            source.RepositoryId,
            source.DefaultBranch,
            source.RootPath,
            source.SymbolMode,
            source.IsEnabled ? "enabled" : "disabled",
            source.LatestSnapshot is null
                ? null
                : new ProCursorLatestSnapshotResponse(
                    source.LatestSnapshot.Id,
                    source.LatestSnapshot.BranchName,
                    source.LatestSnapshot.CommitSha,
                    source.LatestSnapshot.SupportsSymbolQueries,
                    source.LatestSnapshot.FreshnessStatus,
                    source.LatestSnapshot.CompletedAt),
            source.OrganizationScopeId,
            source.CanonicalSourceRef,
            source.SourceDisplayName);
    }

    private static ProCursorTrackedBranchResponse MapTrackedBranch(ProCursorTrackedBranchDto branch)
    {
        return new ProCursorTrackedBranchResponse(
            branch.Id,
            branch.BranchName,
            branch.RefreshTriggerMode,
            branch.MiniIndexEnabled,
            branch.LastSeenCommitSha,
            branch.LastIndexedCommitSha,
            branch.IsEnabled,
            branch.FreshnessStatus);
    }

    private static ProCursorRefreshResponse MapRefresh(ProCursorIndexJobDto job)
    {
        return new ProCursorRefreshResponse(
            job.Id,
            job.KnowledgeSourceId,
            job.TrackedBranchId,
            job.BranchName,
            job.JobKind,
            job.Status.ToString().ToLowerInvariant(),
            job.RequestedCommitSha,
            job.QueuedAt);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ProCursor source {SourceId} created for client {ClientId}")]
    private static partial void LogSourceCreated(ILogger logger, Guid clientId, Guid sourceId);
}

file sealed record PremiumFeatureUnavailablePayload(string Error, string Feature, string Message);

/// <summary>Response payload for a ProCursor knowledge source.</summary>
public sealed record ProCursorKnowledgeSourceResponse(
    Guid SourceId,
    string DisplayName,
    ProCursorSourceKind SourceKind,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    string DefaultBranch,
    string? RootPath,
    string SymbolMode,
    string Status,
    ProCursorLatestSnapshotResponse? LatestSnapshot,
    Guid? OrganizationScopeId = null,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? SourceDisplayName = null);

/// <summary>Response payload describing the latest snapshot for a ProCursor source.</summary>
public sealed record ProCursorLatestSnapshotResponse(
    Guid SnapshotId,
    string Branch,
    string CommitSha,
    bool SupportsSymbolQueries,
    string FreshnessStatus,
    DateTimeOffset? CompletedAt);

/// <summary>Response payload for one tracked branch.</summary>
public sealed record ProCursorTrackedBranchResponse(
    Guid BranchId,
    string BranchName,
    ProCursorRefreshTriggerMode RefreshTriggerMode,
    bool MiniIndexEnabled,
    string? LastSeenCommitSha,
    string? LastIndexedCommitSha,
    bool IsEnabled,
    string FreshnessStatus);

/// <summary>Response payload returned when a refresh job is queued.</summary>
public sealed record ProCursorRefreshResponse(
    Guid JobId,
    Guid SourceId,
    Guid TrackedBranchId,
    string BranchName,
    string JobKind,
    string Status,
    string? RequestedCommitSha,
    DateTimeOffset QueuedAt);
