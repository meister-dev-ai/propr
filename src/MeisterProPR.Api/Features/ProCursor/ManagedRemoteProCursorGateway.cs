// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;

namespace MeisterProPR.Api.Features.ProCursor;

/// <summary>
///     ProPR-side gateway used in managed-remote mode.
///     Control-plane source state remains local while execution delegates to the extracted service.
/// </summary>
public sealed class ManagedRemoteProCursorGateway(
    IProCursorKnowledgeSourceRepository knowledgeSourceRepository,
    HttpProCursorGateway remoteGateway) : IProCursorGateway
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(Guid clientId, CancellationToken ct = default)
        => remoteGateway.ListSourcesAsync(clientId, ct);

    /// <inheritdoc />
    public Task<ProCursorKnowledgeSourceDto> CreateSourceAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct = default)
        => this.CreateSourceAndInvalidateAsync(clientId, request, ct);

    /// <inheritdoc />
    public Task<ProCursorIndexJobDto> QueueRefreshAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct = default)
        => remoteGateway.QueueRefreshAsync(clientId, sourceId, request, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ProCursorTrackedBranchDto>> ListTrackedBranchesAsync(
        Guid clientId,
        Guid sourceId,
        CancellationToken ct = default)
        => remoteGateway.ListTrackedBranchesAsync(clientId, sourceId, ct);

    /// <inheritdoc />
    public Task<ProCursorTrackedBranchDto> AddTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct = default)
        => this.AddTrackedBranchAndInvalidateAsync(clientId, sourceId, request, ct);

    /// <inheritdoc />
    public Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct = default)
        => this.UpdateTrackedBranchAndInvalidateAsync(clientId, sourceId, trackedBranchId, request, ct);

    /// <inheritdoc />
    public Task<bool> RemoveTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default)
        => this.RemoveTrackedBranchAndInvalidateAsync(clientId, sourceId, trackedBranchId, ct);

    /// <inheritdoc />
    public Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct = default)
        => remoteGateway.AskKnowledgeAsync(request, ct);

    /// <inheritdoc />
    public Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
        => remoteGateway.GetSymbolInsightAsync(request, ct);

    private async Task<ProCursorKnowledgeSourceDto> CreateSourceAndInvalidateAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct)
    {
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
        await remoteGateway.InvalidateRuntimeConfigurationAsync(source.Id, ct);
        return MapSource(source);
    }

    private async Task<ProCursorTrackedBranchDto> AddTrackedBranchAndInvalidateAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct)
    {
        var source = await knowledgeSourceRepository.GetByIdAsync(clientId, sourceId, ct);
        if (source is null)
        {
            throw new KeyNotFoundException($"ProCursor source {sourceId} was not found for client {clientId}.");
        }

        var branch = source.AddTrackedBranch(
            Guid.NewGuid(),
            request.BranchName,
            request.RefreshTriggerMode,
            request.MiniIndexEnabled);

        await knowledgeSourceRepository.UpdateAsync(source, ct);
        await remoteGateway.InvalidateRuntimeConfigurationAsync(sourceId, ct);
        return MapTrackedBranch(branch);
    }

    private async Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAndInvalidateAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct)
    {
        var source = await knowledgeSourceRepository.GetByIdAsync(clientId, sourceId, ct);
        if (source is null)
        {
            throw new KeyNotFoundException($"ProCursor source {sourceId} was not found for client {clientId}.");
        }

        var branch = source.TrackedBranches.FirstOrDefault(candidate => candidate.Id == trackedBranchId);
        if (branch is null)
        {
            return null;
        }

        branch.UpdateSettings(
            request.RefreshTriggerMode ?? branch.RefreshTriggerMode,
            request.MiniIndexEnabled ?? branch.MiniIndexEnabled,
            request.IsEnabled);

        await knowledgeSourceRepository.UpdateAsync(source, ct);
        await remoteGateway.InvalidateRuntimeConfigurationAsync(sourceId, ct);
        return MapTrackedBranch(branch);
    }

    private async Task<bool> RemoveTrackedBranchAndInvalidateAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct)
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

        var removed = await knowledgeSourceRepository.DeleteTrackedBranchAsync(clientId, sourceId, trackedBranchId, ct);
        if (removed)
        {
            await remoteGateway.InvalidateRuntimeConfigurationAsync(sourceId, ct);
        }

        return removed;
    }

    private static ProCursorKnowledgeSourceDto MapSource(ProCursorKnowledgeSource source)
    {
        var canonicalSourceRef = !string.IsNullOrWhiteSpace(source.CanonicalSourceProvider) &&
                                 !string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
            ? new CanonicalSourceReferenceDto(source.CanonicalSourceProvider, source.CanonicalSourceValue)
            : new CanonicalSourceReferenceDto("azureDevOps", source.RepositoryId);

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
            null,
            source.TrackedBranches
                .OrderBy(branch => branch.BranchName, StringComparer.OrdinalIgnoreCase)
                .Select(MapTrackedBranch)
                .ToList()
                .AsReadOnly(),
            source.OrganizationScopeId,
            canonicalSourceRef,
            string.IsNullOrWhiteSpace(source.SourceDisplayName) ? source.RepositoryId : source.SourceDisplayName);
    }

    private static ProCursorTrackedBranchDto MapTrackedBranch(ProCursorTrackedBranch trackedBranch)
    {
        return new ProCursorTrackedBranchDto(
            trackedBranch.Id,
            trackedBranch.BranchName,
            trackedBranch.RefreshTriggerMode,
            trackedBranch.MiniIndexEnabled,
            trackedBranch.LastSeenCommitSha,
            trackedBranch.LastIndexedCommitSha,
            trackedBranch.IsEnabled,
            "unknown");
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
