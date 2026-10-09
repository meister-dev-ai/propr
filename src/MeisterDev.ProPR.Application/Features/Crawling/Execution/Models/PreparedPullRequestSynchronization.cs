// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;

/// <summary>Configuration fields authorized immediately before review intake.</summary>
public sealed record PullRequestReviewSettings(
    ProCursorSourceScopeMode ProCursorSourceScopeMode,
    IReadOnlyList<Guid> ProCursorSourceIds,
    IReadOnlyList<Guid> InvalidProCursorSourceIds,
    float? ReviewTemperature);

/// <summary>A provider-prepared synchronization pass whose review intake can be completed once.</summary>
/// <remarks>
///     Completion accepts configuration fields only. Client, provider, repository, review and revision
///     remain bound to the provider observations captured during preparation.
/// </remarks>
public sealed class PreparedPullRequestSynchronization(
    PullRequestSynchronizationRequest request,
    Func<PullRequestSynchronizationRequest, CancellationToken, Task<PullRequestSynchronizationOutcome>> complete)
{
    private int _completed;

    /// <summary>Completes database-only review intake using the caller's current authorized settings.</summary>
    public Task<PullRequestSynchronizationOutcome> CompleteAsync(
        PullRequestReviewSettings? settings = null,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref this._completed, 1) != 0)
        {
            throw new InvalidOperationException("A prepared synchronization pass can be completed only once.");
        }

        ct.ThrowIfCancellationRequested();
        var authorized = settings is null
            ? request
            : request with
            {
                ProCursorSourceScopeMode = settings.ProCursorSourceScopeMode,
                ProCursorSourceIds = settings.ProCursorSourceIds.ToArray(),
                InvalidProCursorSourceIds = settings.InvalidProCursorSourceIds.ToArray(),
                ReviewTemperature = settings.ReviewTemperature,
            };
        return complete(authorized, ct);
    }
}
