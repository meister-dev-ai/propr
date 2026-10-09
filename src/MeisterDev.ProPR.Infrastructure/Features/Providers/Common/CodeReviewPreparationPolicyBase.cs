// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Provides captured-revision validation for providers whose review coordinates contain commit evidence.</summary>
internal abstract class CodeReviewPreparationPolicyBase : ICodeReviewPreparationPolicy
{
    public abstract ScmProvider Provider { get; }

    public virtual ThreadResolutionIntent InterpretThreadResolution(string? status) =>
        SavedThreadStatusCompatibilityDecoder.Decode(status);

    public virtual PreparedReviewResult PrepareResult(PullRequest pullRequest, ReviewResult result) => new(result);
    public virtual object? CreatePublicationContext(int? compareToIterationId) => null;

    public virtual ReviewComparisonHandle SelectComparisonHandle(ReviewJob job, ReviewJob baseline) =>
        new(true, CompareToRevision: baseline.ReviewRevisionReference);

    public virtual ReviewRevision ResolveStoredRevision(ReviewJob job) => job.ReviewRevisionReference
                                                                          ?? throw new InvalidOperationException(
                                                                              $"Review job {job.Id} is missing normalized review revision data for provider {job.Provider}.");

    public virtual bool RequiresLiveRevisionRefresh(ReviewRevision? revision) => revision is null
                                                                                 || !LooksLikeCommitSha(revision.HeadSha)
                                                                                 || !LooksLikeCommitSha(revision.BaseSha)
                                                                                 || revision.StartSha is not null && !LooksLikeCommitSha(revision.StartSha);

    private static bool LooksLikeCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        return candidate.Length is >= 7 and <= 64 && candidate.All(Uri.IsHexDigit);
    }
}
