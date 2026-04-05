// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>
///     In-process application facade for the ProCursor bounded module.
/// </summary>
public sealed partial class ProCursorGateway(
    IClientAdminService clientAdminService,
    IClientAdoOrganizationScopeRepository organizationScopeRepository,
    IAdoDiscoveryService adoDiscoveryService,
    IProCursorKnowledgeSourceRepository knowledgeSourceRepository,
    IProCursorIndexSnapshotRepository snapshotRepository,
    ProCursorQueryService queryService,
    ProCursorIndexCoordinator indexCoordinator,
    ILogger<ProCursorGateway> logger) : IProCursorGateway
{
    private const string AzureDevOpsProvider = "azureDevOps";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(Guid clientId, CancellationToken ct = default)
    {
        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            throw new KeyNotFoundException($"Client {clientId} was not found.");
        }

        var sources = await knowledgeSourceRepository.ListByClientAsync(clientId, ct);
        var sourceDtos = new List<ProCursorKnowledgeSourceDto>(sources.Count);

        foreach (var source in sources)
        {
            var snapshotsBySource = await snapshotRepository.ListBySourceAsync(source.Id, ct);
            var latestSnapshot = snapshotsBySource
                .OrderByDescending(snapshot => snapshot.CreatedAt)
                .ThenByDescending(snapshot => snapshot.CompletedAt)
                .FirstOrDefault();

            sourceDtos.Add(MapSource(source, latestSnapshot, snapshotsBySource));
        }

        return sourceDtos.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<ProCursorKnowledgeSourceDto> CreateSourceAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            throw new KeyNotFoundException($"Client {clientId} was not found.");
        }

        if (request.TrackedBranches.Count == 0)
        {
            throw new InvalidOperationException("At least one tracked branch is required.");
        }

        var resolvedSource = await this.ResolveSourceSelectionAsync(clientId, request, ct);

        var duplicateExists = await knowledgeSourceRepository.ExistsAsync(
            clientId,
            request.SourceKind,
            resolvedSource.OrganizationUrl,
            request.ProjectId.Trim(),
            resolvedSource.RepositoryId,
            request.RootPath,
            ct);

        if (duplicateExists)
        {
            throw new InvalidOperationException("A ProCursor source with the same coordinates already exists for this client.");
        }

        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            clientId,
            request.DisplayName,
            request.SourceKind,
            resolvedSource.OrganizationUrl,
            request.ProjectId.Trim(),
            resolvedSource.RepositoryId,
            request.DefaultBranch,
            request.RootPath,
            true,
            request.SymbolMode,
            resolvedSource.OrganizationScopeId,
            resolvedSource.CanonicalSourceRef?.Provider,
            resolvedSource.CanonicalSourceRef?.Value,
            resolvedSource.SourceDisplayName);

        foreach (var trackedBranch in request.TrackedBranches)
        {
            source.AddTrackedBranch(
                Guid.NewGuid(),
                trackedBranch.BranchName,
                trackedBranch.RefreshTriggerMode,
                trackedBranch.MiniIndexEnabled);
        }

        await knowledgeSourceRepository.AddAsync(source, ct);
        return MapSource(source, null, []);
    }

    /// <inheritdoc />
    public Task<ProCursorIndexJobDto> QueueRefreshAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct = default)
    {
        return indexCoordinator.QueueRefreshAsync(clientId, sourceId, request, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorTrackedBranchDto>> ListTrackedBranchesAsync(
        Guid clientId,
        Guid sourceId,
        CancellationToken ct = default)
    {
        var source = await knowledgeSourceRepository.GetByIdAsync(clientId, sourceId, ct);
        if (source is null)
        {
            throw new KeyNotFoundException($"ProCursor source {sourceId} was not found for client {clientId}.");
        }

        var snapshots = await snapshotRepository.ListBySourceAsync(source.Id, ct);

        return source.TrackedBranches
            .OrderBy(branch => branch.BranchName, StringComparer.OrdinalIgnoreCase)
            .Select(branch => MapTrackedBranch(
                branch,
                snapshots.FirstOrDefault(snapshot => snapshot.TrackedBranchId == branch.Id)))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<ProCursorTrackedBranchDto> AddTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await knowledgeSourceRepository.GetByIdAsync(clientId, sourceId, ct);
        if (source is null)
        {
            throw new KeyNotFoundException($"ProCursor source {sourceId} was not found for client {clientId}.");
        }

        var trackedBranch = source.AddTrackedBranch(
            Guid.NewGuid(),
            request.BranchName,
            request.RefreshTriggerMode,
            request.MiniIndexEnabled);

        await knowledgeSourceRepository.UpdateAsync(source, ct);
        return MapTrackedBranch(trackedBranch, null);
    }

    /// <inheritdoc />
    public async Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await knowledgeSourceRepository.GetByIdAsync(clientId, sourceId, ct);
        if (source is null)
        {
            throw new KeyNotFoundException($"ProCursor source {sourceId} was not found for client {clientId}.");
        }

        var trackedBranch = source.TrackedBranches.FirstOrDefault(branch => branch.Id == trackedBranchId);
        if (trackedBranch is null)
        {
            return null;
        }

        trackedBranch.UpdateSettings(
            request.RefreshTriggerMode ?? trackedBranch.RefreshTriggerMode,
            request.MiniIndexEnabled ?? trackedBranch.MiniIndexEnabled,
            request.IsEnabled);

        await knowledgeSourceRepository.UpdateAsync(source, ct);
        var latestSnapshot = await snapshotRepository.GetLatestAsync(source.Id, trackedBranch.Id, ct);
        return MapTrackedBranch(trackedBranch, latestSnapshot);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default)
    {
        var source = await knowledgeSourceRepository.GetByIdAsync(clientId, sourceId, ct);
        if (source is null)
        {
            throw new KeyNotFoundException($"ProCursor source {sourceId} was not found for client {clientId}.");
        }

        if (source.TrackedBranches.Count <= 1)
        {
            throw new InvalidOperationException("The last tracked branch cannot be removed.");
        }

        return await knowledgeSourceRepository.DeleteTrackedBranchAsync(clientId, sourceId, trackedBranchId, ct);
    }

    /// <inheritdoc />
    public Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.LogKnowledgeQueryAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.LogSymbolQueryAsync(request, ct);
    }

    private static ProCursorKnowledgeSourceDto MapSource(
        ProCursorKnowledgeSource source,
        ProCursorIndexSnapshot? latestSnapshot,
        IReadOnlyList<ProCursorIndexSnapshot> snapshotsBySource)
    {
        var canonicalSourceRef = !string.IsNullOrWhiteSpace(source.CanonicalSourceProvider) &&
                                 !string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
            ? new CanonicalSourceReferenceDto(source.CanonicalSourceProvider, source.CanonicalSourceValue)
            : new CanonicalSourceReferenceDto(AzureDevOpsProvider, source.RepositoryId);

        return new ProCursorKnowledgeSourceDto(
            source.Id,
            source.ClientId,
            source.DisplayName,
            source.SourceKind,
            source.OrganizationUrl,
            source.ProjectId,
            source.RepositoryId,
            source.DefaultBranch,
            source.RootPath,
            source.IsEnabled,
            source.SymbolMode,
            MapSnapshot(latestSnapshot, source.TrackedBranches),
            source.TrackedBranches
                .OrderBy(branch => branch.BranchName, StringComparer.OrdinalIgnoreCase)
                .Select(branch => MapTrackedBranch(
                    branch,
                    snapshotsBySource.FirstOrDefault(snapshot => snapshot.TrackedBranchId == branch.Id)))
                .ToList()
                .AsReadOnly(),
            source.OrganizationScopeId,
            canonicalSourceRef,
            string.IsNullOrWhiteSpace(source.SourceDisplayName) ? source.RepositoryId : source.SourceDisplayName);
    }

    private async Task<ResolvedSourceSelection> ResolveSourceSelectionAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct)
    {
        if (request.OrganizationScopeId.HasValue)
        {
            return await this.ResolveGuidedSourceSelectionAsync(clientId, request, ct);
        }

        if (string.IsNullOrWhiteSpace(request.OrganizationUrl) || string.IsNullOrWhiteSpace(request.RepositoryId))
        {
            throw new InvalidOperationException(
                "Legacy source creation requires organizationUrl and repositoryId when organizationScopeId is not provided.");
        }

        var canonicalSourceRef = request.CanonicalSourceRef ?? new CanonicalSourceReferenceDto(AzureDevOpsProvider, request.RepositoryId.Trim());
        var sourceDisplayName = NormalizeOptional(request.SourceDisplayName) ?? request.RepositoryId.Trim();

        return new ResolvedSourceSelection(
            request.OrganizationUrl.Trim(),
            request.RepositoryId.Trim(),
            null,
            canonicalSourceRef,
            sourceDisplayName);
    }

    private async Task<ResolvedSourceSelection> ResolveGuidedSourceSelectionAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct)
    {
        if (!request.OrganizationScopeId.HasValue)
        {
            throw new InvalidOperationException("OrganizationScopeId is required for guided source selection.");
        }

        var canonicalSourceRef = request.CanonicalSourceRef
            ?? throw new InvalidOperationException("CanonicalSourceRef is required for guided source selection.");

        var scope = await organizationScopeRepository.GetByIdAsync(clientId, request.OrganizationScopeId.Value, ct);
        if (scope is null)
        {
            LogGuidedOrganizationScopeMissing(logger, clientId, request.OrganizationScopeId.Value);
            throw new KeyNotFoundException(
                $"Organization scope {request.OrganizationScopeId.Value} was not found for client {clientId}.");
        }

        if (!scope.IsEnabled)
        {
            LogGuidedOrganizationScopeDisabled(logger, clientId, scope.Id);
            throw new InvalidOperationException("The selected organization scope is disabled.");
        }

        var projectId = request.ProjectId.Trim();
        var availableSources = await adoDiscoveryService.ListSourcesAsync(
            clientId,
            scope.Id,
            projectId,
            request.SourceKind,
            ct);

        var sourceOption = availableSources.FirstOrDefault(option =>
            string.Equals(option.CanonicalSourceRef.Provider, canonicalSourceRef.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.CanonicalSourceRef.Value, canonicalSourceRef.Value, StringComparison.OrdinalIgnoreCase));

        if (sourceOption is null)
        {
            LogGuidedSourceUnavailable(logger, clientId, projectId, canonicalSourceRef.Value);
            throw new InvalidOperationException("The selected source is no longer available in Azure DevOps.");
        }

        var availableBranches = await adoDiscoveryService.ListBranchesAsync(
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
            LogGuidedBranchValidationFailed(logger, clientId, sourceDisplayName, branchValidationError);
            throw new InvalidOperationException(branchValidationError);
        }

        return new ResolvedSourceSelection(
            scope.OrganizationUrl,
            canonicalSourceRef.Value,
            scope.Id,
            canonicalSourceRef,
            sourceDisplayName);
    }

    private static string? ValidateBranchSelection(
        ProCursorKnowledgeSourceRegistrationRequest request,
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

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ResolvedSourceSelection(
        string OrganizationUrl,
        string RepositoryId,
        Guid? OrganizationScopeId,
        CanonicalSourceReferenceDto? CanonicalSourceRef,
        string? SourceDisplayName);

    private static ProCursorSnapshotDto? MapSnapshot(
        ProCursorIndexSnapshot? snapshot,
        IEnumerable<ProCursorTrackedBranch> trackedBranches)
    {
        if (snapshot is null)
        {
            return null;
        }

        var branchName = trackedBranches.FirstOrDefault(branch => branch.Id == snapshot.TrackedBranchId)?.BranchName ?? string.Empty;
        var trackedBranch = trackedBranches.FirstOrDefault(branch => branch.Id == snapshot.TrackedBranchId);
        var freshnessStatus = ProCursorFreshnessEvaluator.GetSnapshotFreshnessStatus(trackedBranch, snapshot);

        return new ProCursorSnapshotDto(
            snapshot.Id,
            snapshot.KnowledgeSourceId,
            snapshot.TrackedBranchId,
            branchName,
            snapshot.CommitSha,
            snapshot.Status,
            snapshot.SupportsSymbolQueries,
            snapshot.FileCount,
            snapshot.ChunkCount,
            snapshot.SymbolCount,
            snapshot.CreatedAt,
            snapshot.CompletedAt,
            snapshot.FailureReason,
            freshnessStatus);
    }

    private static ProCursorTrackedBranchDto MapTrackedBranch(
        ProCursorTrackedBranch trackedBranch,
        ProCursorIndexSnapshot? latestSnapshot)
    {
        return new ProCursorTrackedBranchDto(
            trackedBranch.Id,
            trackedBranch.BranchName,
            trackedBranch.RefreshTriggerMode,
            trackedBranch.MiniIndexEnabled,
            trackedBranch.LastSeenCommitSha,
            trackedBranch.LastIndexedCommitSha,
                trackedBranch.IsEnabled,
                ProCursorFreshnessEvaluator.GetBranchFreshnessStatus(trackedBranch, latestSnapshot));
    }

    private async Task<ProCursorKnowledgeAnswerDto> LogKnowledgeQueryAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct)
    {
        var response = await queryService.AskKnowledgeAsync(request, ct);
        if (string.Equals(response.Status, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            LogKnowledgeUnavailable(logger, request.ClientId, request.Question);
        }

        return response;
    }

    private async Task<ProCursorSymbolInsightDto> LogSymbolQueryAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct)
    {
        var response = await queryService.GetSymbolInsightAsync(request, ct);
        if (string.Equals(response.Status, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            LogSymbolUnavailable(logger, request.ClientId, request.Symbol);
        }

        return response;
    }
}
