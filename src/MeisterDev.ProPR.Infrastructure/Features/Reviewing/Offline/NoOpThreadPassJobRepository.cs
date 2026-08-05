// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Stands in for the thread-pass store where there is no database.
/// </summary>
/// <remarks>
///     Offline execution and the API's own test host both run the file pass alone: no pull-request conversation
///     is fetched, so no pass is ever created. Every claim here is refused rather than granted, so anything that
///     did reach it would decline to act instead of acting untracked, and the read paths that list a pull
///     request's passes see an empty pull request rather than failing to resolve at all.
/// </remarks>
public sealed class NoOpThreadPassJobRepository : IThreadPassJobRepository
{
    /// <inheritdoc />
    public Task<TryClaimThreadPassResult> TryClaimAsync(ThreadPassJob job, CancellationToken ct = default)
    {
        return Task.FromResult(new TryClaimThreadPassResult(false, null));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadPassJob>> GetPendingAsync(int maxCount, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadPassJob>>([]);
    }

    /// <inheritdoc />
    public Task<bool> TryBeginAttemptAsync(Guid jobId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<bool> SetCompletedAsync(Guid jobId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<bool> SetSkippedAsync(Guid jobId, string reason, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task SetCancelledAsync(Guid jobId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetBudgetHeldAsync(
        Guid jobId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetBudgetExceededAsync(
        Guid jobId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryRestartAsync(Guid jobId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task SetAiConfigAsync(Guid jobId, Guid? connectionId, string? model, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ThreadPassJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        return Task.FromResult<ThreadPassJob?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadPassJob>> GetForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        int maxCount,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadPassJob>>([]);
    }

    /// <inheritdoc />
    public Task<bool> RecordAttemptFailureAsync(Guid jobId, string errorMessage, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<int> CancelActiveForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    public Task<StalledThreadPassSweep> ReclaimStalledAsync(
        TimeSpan stalledAfter,
        CancellationToken ct = default)
    {
        return Task.FromResult(new StalledThreadPassSweep(0, 0));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadPassHandledThreadKey>> GetHandledThreadKeysAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadPassHandledThreadKey>>([]);
    }

    /// <inheritdoc />
    public Task RecordHandledThreadAsync(
        Guid jobId,
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string threadId,
        int observedReplyCount,
        string revisionKey,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
