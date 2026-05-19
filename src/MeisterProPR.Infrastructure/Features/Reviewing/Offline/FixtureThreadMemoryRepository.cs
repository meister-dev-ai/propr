// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Scenario-backed thread-memory repository for offline review execution.
/// </summary>
public sealed class FixtureThreadMemoryRepository(IReviewEvaluationFixtureAccessor fixtureAccessor) : IThreadMemoryRepository
{
    public Task UpsertAsync(ThreadMemoryRecord record, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task BulkUpsertAsync(IEnumerable<ThreadMemoryRecord> records, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<bool> RemoveByThreadAsync(Guid clientId, string repositoryId, long threadId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<PagedResult<ThreadMemoryRecord>> GetPagedAsync(
        Guid clientId,
        string? search,
        int page,
        int pageSize,
        MemorySource? source = null,
        string? repositoryId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        return Task.FromResult(new PagedResult<ThreadMemoryRecord>([], 0, page, pageSize));
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarAsync(
        Guid clientId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>(this.GetMatches(topN, null));
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(
        Guid clientId,
        string repositoryId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>(this.GetMatches(topN, filePath));
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarInPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>(this.GetMatches(topN, null));
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>(this.GetMatches(topN, filePath));
    }

    private IReadOnlyList<ThreadMemoryMatchDto> GetMatches(int topN, string? filePath)
    {
        var matches = fixtureAccessor.Scenario?.ThreadMemory?.MatchesOrEmpty ?? [];

        var projected = matches.Select((match, index) => new ThreadMemoryMatchDto(
            match.MemoryRecordId ?? CreateDeterministicGuid(fixtureAccessor.Fixture?.FixtureId, fixtureAccessor.ScenarioId, match.ThreadId, index),
            match.ThreadId,
            match.FilePath,
            match.ResolutionSummary,
            match.SimilarityScore,
            match.MatchSource,
            match.Source));

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            projected = projected.Where(match =>
                string.Equals(match.FilePath, filePath, StringComparison.Ordinal));
        }

        return projected
            .OrderByDescending(match => match.SimilarityScore)
            .ThenBy(match => match.ThreadId)
            .Take(Math.Max(0, topN))
            .ToList();
    }

    private static Guid CreateDeterministicGuid(string? fixtureId, string? scenarioId, long threadId, int index)
    {
        var seed = $"{fixtureId ?? string.Empty}|{scenarioId ?? string.Empty}|{threadId}|{index}";
        var bytes = Encoding.UTF8.GetBytes(seed);
        var guidBytes = new byte[16];
        for (var position = 0; position < bytes.Length; position++)
        {
            guidBytes[position % guidBytes.Length] ^= bytes[position];
        }

        return new Guid(guidBytes);
    }
}
