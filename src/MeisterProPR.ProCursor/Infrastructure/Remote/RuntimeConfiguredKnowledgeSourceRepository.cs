// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.ProCursor.Options;
using Microsoft.Extensions.Options;

namespace MeisterProPR.ProCursor.Infrastructure.Remote;

/// <summary>
///     In-memory knowledge-source repository hydrated from ProPR runtime configuration projections.
/// </summary>
public sealed class RuntimeConfiguredKnowledgeSourceRepository(
    IProCursorRuntimeConfigurationBroker runtimeConfigurationBroker,
    IOptions<ProCursorHostOptions> hostOptions)
    : IProCursorKnowledgeSourceRepository, IProCursorRuntimeConfigurationCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, CacheEntry> _sourcesById = [];
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(Math.Max(1, hostOptions.Value.RuntimeConfigurationTtlSeconds));
    private DateTimeOffset? _lastFullRefreshAtUtc;

    public async Task<IReadOnlyList<ProCursorKnowledgeSource>> ListEnabledAsync(CancellationToken ct = default)
    {
        await this.EnsureAllFreshAsync(ct);
        lock (this._lock)
        {
            return this._sourcesById.Values
                .Select(entry => Clone(entry.Source))
                .Where(source => source.IsEnabled)
                .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }
    }

    public async Task<IReadOnlyList<ProCursorKnowledgeSource>> ListByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        await this.EnsureAllFreshAsync(ct);
        lock (this._lock)
        {
            return this._sourcesById.Values
                .Select(entry => Clone(entry.Source))
                .Where(source => source.ClientId == clientId)
                .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }
    }

    public async Task<ProCursorKnowledgeSource?> GetBySourceIdAsync(Guid sourceId, CancellationToken ct = default)
    {
        var source = await this.GetOrRefreshAsync(sourceId, ct);
        return source is null ? null : Clone(source);
    }

    public async Task<ProCursorKnowledgeSource?> GetByIdAsync(Guid clientId, Guid sourceId, CancellationToken ct = default)
    {
        var source = await this.GetOrRefreshAsync(sourceId, ct);
        return source is null || source.ClientId != clientId ? null : Clone(source);
    }

    public async Task<ProCursorKnowledgeSource?> GetByRepositoryAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        CancellationToken ct = default)
    {
        await this.EnsureAllFreshAsync(ct);
        lock (this._lock)
        {
            var source = this._sourcesById.Values
                .Select(entry => entry.Source)
                .FirstOrDefault(candidate =>
                    candidate.ClientId == clientId &&
                    string.Equals(candidate.ProviderScopePath, organizationUrl, StringComparison.Ordinal) &&
                    string.Equals(candidate.ProviderProjectKey, projectId, StringComparison.Ordinal) &&
                    string.Equals(candidate.RepositoryId, repositoryId, StringComparison.Ordinal));

            return source is null ? null : Clone(source);
        }
    }

    public async Task<bool> ExistsAsync(
        Guid clientId,
        ProCursorSourceKind sourceKind,
        string organizationUrl,
        string projectId,
        string repositoryId,
        string? rootPath,
        CancellationToken ct = default)
    {
        await this.EnsureAllFreshAsync(ct);
        var normalizedRootPath = string.IsNullOrWhiteSpace(rootPath) ? null : rootPath.Trim();

        lock (this._lock)
        {
            return this._sourcesById.Values
                .Select(entry => entry.Source)
                .Any(source =>
                    source.ClientId == clientId &&
                    source.SourceKind == sourceKind &&
                    string.Equals(source.ProviderScopePath, organizationUrl, StringComparison.Ordinal) &&
                    string.Equals(source.ProviderProjectKey, projectId, StringComparison.Ordinal) &&
                    string.Equals(source.RepositoryId, repositoryId, StringComparison.Ordinal) &&
                    string.Equals(source.RootPath, normalizedRootPath, StringComparison.Ordinal));
        }
    }

    public Task AddAsync(ProCursorKnowledgeSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (this._lock)
        {
            this._sourcesById[source.Id] = new CacheEntry(Clone(source), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(ProCursorKnowledgeSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (this._lock)
        {
            this._sourcesById[source.Id] = new CacheEntry(Clone(source), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default)
    {
        lock (this._lock)
        {
            if (!this._sourcesById.TryGetValue(sourceId, out var entry) || entry.Source.ClientId != clientId)
            {
                return Task.FromResult(false);
            }

            var existing = entry.Source.TrackedBranches.FirstOrDefault(branch => branch.Id == trackedBranchId);
            if (existing is null)
            {
                return Task.FromResult(false);
            }

            entry.Source.TrackedBranches.Remove(existing);
            this._sourcesById[sourceId] = entry with { LoadedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }

    public async Task WarmAsync(CancellationToken ct = default)
    {
        var projections = await runtimeConfigurationBroker.ListEnabledAsync(ct);
        var refreshedEntries = projections
            .Select(projection => new CacheEntry(MapSource(projection.Source), projection.ProjectionVersion, projection.FetchedAt))
            .ToList();

        lock (this._lock)
        {
            this._sourcesById.Clear();
            foreach (var entry in refreshedEntries)
            {
                this._sourcesById[entry.Source.Id] = entry;
            }

            this._lastFullRefreshAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public Task InvalidateAsync(Guid sourceId, CancellationToken ct = default)
    {
        lock (this._lock)
        {
            this._sourcesById.Remove(sourceId);
            this._lastFullRefreshAtUtc = null;
        }

        return Task.CompletedTask;
    }

    private async Task EnsureAllFreshAsync(CancellationToken ct)
    {
        var shouldRefresh = false;
        lock (this._lock)
        {
            shouldRefresh = !this._lastFullRefreshAtUtc.HasValue || DateTimeOffset.UtcNow - this._lastFullRefreshAtUtc.Value >= this._ttl;
        }

        if (shouldRefresh)
        {
            await this.WarmAsync(ct);
        }
    }

    private async Task<ProCursorKnowledgeSource?> GetOrRefreshAsync(Guid sourceId, CancellationToken ct)
    {
        CacheEntry? entry;
        lock (this._lock)
        {
            if (this._sourcesById.TryGetValue(sourceId, out var existing)
                && DateTimeOffset.UtcNow - existing.LoadedAt < this._ttl)
            {
                return Clone(existing.Source);
            }

            entry = this._sourcesById.TryGetValue(sourceId, out existing) ? existing : null;
        }

        var request = new ProCursorRuntimeConfigurationRefreshRequest(
            entry is null ? "cacheMiss" : "stale",
            entry?.ProjectionVersion);

        try
        {
            var projection = await runtimeConfigurationBroker.RefreshAsync(sourceId, request, ct);
            var refreshedEntry = new CacheEntry(MapSource(projection.Source), projection.ProjectionVersion, projection.FetchedAt);
            lock (this._lock)
            {
                this._sourcesById[sourceId] = refreshedEntry;
            }

            return Clone(refreshedEntry.Source);
        }
        catch (KeyNotFoundException)
        {
            lock (this._lock)
            {
                this._sourcesById.Remove(sourceId);
            }

            return null;
        }
    }

    private static ProCursorKnowledgeSource MapSource(ProCursorKnowledgeSourceDto source)
    {
        var mapped = new ProCursorKnowledgeSource(
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
            source.OrganizationScopeId,
            source.CanonicalSourceRef?.Provider,
            source.CanonicalSourceRef?.Value,
            source.SourceDisplayName);

        foreach (var branch in source.TrackedBranches)
        {
            var mappedBranch = mapped.AddTrackedBranch(
                branch.Id,
                branch.BranchName,
                branch.RefreshTriggerMode,
                branch.MiniIndexEnabled);

            if (!mappedBranch.IsEnabled && branch.IsEnabled)
            {
                mappedBranch.SetEnabled(true);
            }

            if (!branch.IsEnabled)
            {
                mappedBranch.SetEnabled(false);
            }

            if (!string.IsNullOrWhiteSpace(branch.LastSeenCommitSha))
            {
                mappedBranch.RecordSeenCommit(branch.LastSeenCommitSha);
            }

            if (!string.IsNullOrWhiteSpace(branch.LastIndexedCommitSha))
            {
                mappedBranch.RecordIndexedCommit(branch.LastIndexedCommitSha);
            }
        }

        if (!source.IsEnabled)
        {
            mapped.SetEnabled(false);
        }

        return mapped;
    }

    private static ProCursorKnowledgeSource Clone(ProCursorKnowledgeSource source)
    {
        return MapSource(
            new ProCursorKnowledgeSourceDto(
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
                    .Select(branch => new ProCursorTrackedBranchDto(
                        branch.Id,
                        branch.BranchName,
                        branch.RefreshTriggerMode,
                        branch.MiniIndexEnabled,
                        branch.LastSeenCommitSha,
                        branch.LastIndexedCommitSha,
                        branch.IsEnabled,
                        "unknown"))
                    .ToList()
                    .AsReadOnly(),
                source.OrganizationScopeId,
                string.IsNullOrWhiteSpace(source.CanonicalSourceProvider) || string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
                    ? null
                    : new CanonicalSourceReferenceDto(source.CanonicalSourceProvider, source.CanonicalSourceValue),
                source.SourceDisplayName));
    }

    private sealed record CacheEntry(ProCursorKnowledgeSource Source, string ProjectionVersion, DateTimeOffset LoadedAt);
}
