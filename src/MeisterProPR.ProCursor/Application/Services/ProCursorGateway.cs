// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.ProCursor.Core;

/// <summary>
///     In-process application facade for the ProCursor bounded module.
/// </summary>
public sealed partial class ProCursorGateway(
    IProCursorKnowledgeSourceRepository knowledgeSourceRepository,
    IProCursorIndexSnapshotRepository snapshotRepository,
    ProCursorQueryService queryService,
    ProCursorIndexCoordinator indexCoordinator,
    ILogger<ProCursorGateway> logger) : IProCursorGateway
{
    private const string AzureDevOpsProvider = "azureDevOps";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
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

        if (request.TrackedBranches.Count == 0)
        {
            throw new InvalidOperationException("At least one tracked branch is required.");
        }

        var duplicateExists = await knowledgeSourceRepository.ExistsAsync(
            clientId,
            request.SourceKind,
            request.ProviderScopePath?.Trim() ?? string.Empty,
            request.ProviderProjectKey.Trim(),
            request.RepositoryId?.Trim() ?? string.Empty,
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
            request.ProviderScopePath?.Trim() ?? string.Empty,
            request.ProviderProjectKey.Trim(),
            request.RepositoryId?.Trim() ?? string.Empty,
            request.DefaultBranch,
            request.RootPath,
            true,
            request.SymbolMode,
            request.OrganizationScopeId,
            request.CanonicalSourceRef?.Provider,
            request.CanonicalSourceRef?.Value,
            NormalizeOptional(request.SourceDisplayName) ?? request.RepositoryId?.Trim());

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
        return this.QueueRefreshInternalAsync(clientId, sourceId, request, ct);
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
        return this.AskKnowledgeInternalAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.GetSymbolInsightInternalAsync(request, ct);
    }

    private async Task<ProCursorIndexJobDto> QueueRefreshInternalAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct)
    {
        return await indexCoordinator.QueueRefreshAsync(clientId, sourceId, request, ct);
    }

    private async Task<ProCursorKnowledgeAnswerDto> AskKnowledgeInternalAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct)
    {
        return await this.LogKnowledgeQueryAsync(request, ct);
    }

    private async Task<ProCursorSymbolInsightDto> GetSymbolInsightInternalAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct)
    {
        return await this.LogSymbolQueryAsync(request, ct);
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
            source.ProviderScopePath,
            source.ProviderProjectKey,
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

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static ProCursorSnapshotDto? MapSnapshot(
        ProCursorIndexSnapshot? snapshot,
        IEnumerable<ProCursorTrackedBranch> trackedBranches)
    {
        if (snapshot is null)
        {
            return null;
        }

        var branchName = trackedBranches.FirstOrDefault(branch => branch.Id == snapshot.TrackedBranchId)?.BranchName ??
                         string.Empty;
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
