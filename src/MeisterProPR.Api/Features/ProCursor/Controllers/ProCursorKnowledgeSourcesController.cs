// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.ProCursor.Controllers;

/// <summary>
///     Client-scoped admin endpoints for managing ProCursor knowledge sources and tracked branches.
/// </summary>
[ApiController]
public sealed partial class ProCursorKnowledgeSourcesController(
    IClientAdminService clientAdminService,
    IScmProviderRegistry providerRegistry,
    IProCursorGateway proCursorGateway,
    ILogger<ProCursorKnowledgeSourcesController> logger) : ControllerBase
{
    /// <summary>
    ///     Returns the ProCursor knowledge sources configured for the given client.
    /// </summary>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/sources")]
    [ProducesResponseType(typeof(IReadOnlyList<ProCursorKnowledgeSourceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListSources(Guid clientId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        try
        {
            if (!await clientAdminService.ExistsAsync(clientId, ct))
            {
                return this.NotFound();
            }

            var sources = await proCursorGateway.ListSourcesAsync(clientId, ct);
            return this.Ok(sources.Select(MapSource).ToList().AsReadOnly());
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Creates a new ProCursor repository or git-backed wiki source for the given client.
    /// </summary>
    [HttpPost("/admin/clients/{clientId:guid}/procursor/sources")]
    [ProducesResponseType(typeof(ProCursorKnowledgeSourceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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

        try
        {
            if (!await clientAdminService.ExistsAsync(clientId, ct))
            {
                return this.NotFound();
            }

            var resolvedRequest = await this.ResolveRegistrationRequestAsync(clientId, request, ct);
            var source = await proCursorGateway.CreateSourceAsync(
                clientId,
                resolvedRequest,
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
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Queues a durable ProCursor index job for the selected tracked branch or the source default branch.
    /// </summary>
    [HttpPost("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/refresh")]
    [ProducesResponseType(typeof(ProCursorRefreshResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Returns the tracked branches configured for the given ProCursor source.
    /// </summary>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches")]
    [ProducesResponseType(typeof(IReadOnlyList<ProCursorTrackedBranchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListTrackedBranches(Guid clientId, Guid sourceId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
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
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Adds a tracked branch to an existing ProCursor source.
    /// </summary>
    [HttpPost("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches")]
    [ProducesResponseType(typeof(ProCursorTrackedBranchResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Updates refresh behavior for one tracked branch.
    /// </summary>
    [HttpPut("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches/{branchId:guid}")]
    [ProducesResponseType(typeof(ProCursorTrackedBranchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Removes a tracked branch from a ProCursor source.
    /// </summary>
    [HttpDelete("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/branches/{branchId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
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

    private async Task<ProCursorKnowledgeSourceRegistrationRequest> ResolveRegistrationRequestAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRequest request,
        CancellationToken ct)
    {
        var trackedBranches = request.TrackedBranches
            .Select(branch => new ProCursorTrackedBranchCreateRequest(
                branch.BranchName,
                branch.RefreshTriggerMode,
                branch.MiniIndexEnabled))
            .ToList()
            .AsReadOnly();

        var hasGuidedSelection = request.OrganizationScopeId.HasValue;
        if (!hasGuidedSelection)
        {
            return new ProCursorKnowledgeSourceRegistrationRequest(
                request.DisplayName,
                request.SourceKind,
                request.ProviderScopePath?.Trim(),
                request.ProviderProjectKey,
                request.RepositoryId?.Trim(),
                request.DefaultBranch,
                request.RootPath,
                request.SymbolMode,
                trackedBranches,
                request.OrganizationScopeId,
                request.CanonicalSourceRef,
                NormalizeOptional(request.SourceDisplayName) ?? request.RepositoryId?.Trim());
        }

        var resolvedSource = await this.ResolveGuidedSourceSelectionAsync(clientId, request, ct);
        return new ProCursorKnowledgeSourceRegistrationRequest(
            request.DisplayName,
            request.SourceKind,
            resolvedSource.OrganizationUrl,
            request.ProviderProjectKey,
            resolvedSource.RepositoryId,
            request.DefaultBranch,
            request.RootPath,
            request.SymbolMode,
            trackedBranches,
            resolvedSource.OrganizationScopeId,
            resolvedSource.CanonicalSourceRef,
            resolvedSource.SourceDisplayName);
    }

    private async Task<ResolvedSourceSelection> ResolveGuidedSourceSelectionAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRequest request,
        CancellationToken ct)
    {
        var scopeId = request.OrganizationScopeId
                      ?? throw new InvalidOperationException("OrganizationScopeId is required for guided source selection.");
        var canonicalSourceRef = request.CanonicalSourceRef
                                 ?? throw new InvalidOperationException("CanonicalSourceRef is required for guided source selection.");

        var discoveryService = providerRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps);
        var scope = await discoveryService.GetScopeAsync(clientId, scopeId, ct);
        if (scope is null)
        {
            throw new KeyNotFoundException($"Organization scope {scopeId} was not found for client {clientId}.");
        }

        if (!scope.IsEnabled)
        {
            throw new InvalidOperationException("The selected organization scope is disabled.");
        }

        var projectId = request.ProviderProjectKey.Trim();
        var availableSources = await discoveryService.ListSourcesAsync(clientId, scope.Id, projectId, request.SourceKind, ct);
        var sourceOption = availableSources.FirstOrDefault(option =>
            string.Equals(option.CanonicalSourceRef.Provider, canonicalSourceRef.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(option.CanonicalSourceRef.Value, canonicalSourceRef.Value, StringComparison.OrdinalIgnoreCase));

        if (sourceOption is null)
        {
            throw new InvalidOperationException("The selected source is no longer available in Azure DevOps.");
        }

        var availableBranches = await discoveryService.ListBranchesAsync(
            clientId,
            scope.Id,
            projectId,
            request.SourceKind,
            canonicalSourceRef,
            ct);

        var branchNames = availableBranches
            .Select(branch => branch.BranchName)
            .Where(static branchName => !string.IsNullOrWhiteSpace(branchName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sourceDisplayName = NormalizeOptional(request.SourceDisplayName) ?? sourceOption.DisplayName;
        var branchValidationError = ValidateBranchSelection(request, branchNames, sourceDisplayName);
        if (branchValidationError is not null)
        {
            throw new InvalidOperationException(branchValidationError);
        }

        return new ResolvedSourceSelection(
            scope.ScopePath,
            canonicalSourceRef.Value,
            scope.Id,
            canonicalSourceRef,
            sourceDisplayName);
    }

    private static string? ValidateBranchSelection(
        ProCursorKnowledgeSourceRequest request,
        IReadOnlyCollection<string> availableBranches,
        string sourceDisplayName)
    {
        if (availableBranches.Count == 0)
        {
            return $"The selected source '{sourceDisplayName}' does not currently expose any branches.";
        }

        if (!availableBranches.Contains(request.DefaultBranch.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return $"The selected default branch '{request.DefaultBranch}' is no longer available for source '{sourceDisplayName}'.";
        }

        var invalidTrackedBranches = request.TrackedBranches
            .Select(branch => branch.BranchName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(branchName => !availableBranches.Contains(branchName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (invalidTrackedBranches.Count > 0)
        {
            return $"The selected source '{sourceDisplayName}' no longer exposes tracked branches: {string.Join(", ", invalidTrackedBranches)}.";
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private sealed record ResolvedSourceSelection(
        string OrganizationUrl,
        string RepositoryId,
        Guid? OrganizationScopeId,
        CanonicalSourceReferenceDto? CanonicalSourceRef,
        string? SourceDisplayName);
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
