// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Prepares captured review data without credentials, live adapters or provider requests.</summary>
public interface ICodeReviewPreparationPolicy
{
    ScmProvider Provider { get; }
    PreparedReviewResult PrepareResult(PullRequest pullRequest, ReviewResult result);
    ReviewRevision ResolveStoredRevision(ReviewJob job);
    bool RequiresLiveRevisionRefresh(ReviewRevision? revision);
    ReviewComparisonHandle SelectComparisonHandle(ReviewJob job, ReviewJob baseline);
    object? CreatePublicationContext(int? compareToIterationId);
    ThreadResolutionIntent InterpretThreadResolution(string? status);
}

/// <summary>Contains prepared findings and the number whose inline anchors were unavailable.</summary>
public sealed record PreparedReviewResult(ReviewResult Result, int DowngradedCount = 0);

/// <summary>Contains the native comparison coordinates supplied to the existing fetch capability.</summary>
public sealed record ReviewComparisonHandle(bool IsDeltaScoped, int? CompareToIterationId = null, ReviewRevision? CompareToRevision = null);
