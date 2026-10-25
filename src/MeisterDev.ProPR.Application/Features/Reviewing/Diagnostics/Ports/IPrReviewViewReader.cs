// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;

/// <summary>Reads the summaries used by the pull request review view without loading execution traces.</summary>
public interface IPrReviewViewReader
{
    /// <summary>Returns paged review summaries, contribution samples, thread passes and review watermarks.</summary>
    Task<PrReviewViewData> ReadAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

/// <summary>Summary fields needed to render one pull request, with review jobs limited to the requested page.</summary>
public sealed record PrReviewViewData(
    IReadOnlyList<PrJobSummaryDto> Jobs,
    IReadOnlyList<string> ContributingMemoryPayloads,
    IReadOnlyList<PrThreadPassSummaryDto> ThreadPasses,
    PrReviewWatermark? Watermark);

/// <summary>Revision watermarks used to decide whether the pull request is waiting for a review.</summary>
public sealed record PrReviewWatermark(
    string PendingReviewRevisionKey,
    string LastProcessedCommitId,
    DateTimeOffset? PendingReviewDetectedAt);
