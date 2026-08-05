// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

/// <inheritdoc cref="IMentionReplyProvenanceReconciler" />
public sealed partial class MentionReplyProvenanceReconciler(
    IMentionReplyJobRepository jobRepository,
    IPostedCommentOriginStore postedCommentOriginStore,
    ILogger<MentionReplyProvenanceReconciler> logger) : IMentionReplyProvenanceReconciler
{
    // A backstop on one pass, not an expected volume. Losing a provenance row takes a process death inside the
    // millisecond between completing a job and recording it, so the realistic count is nought or one; a pass
    // that finds hundreds is a symptom to read in the log, not work to grind through unbounded.
    private const int MaxRepliesPerPass = 500;

    // How far back an answer is still worth attributing. Long enough to cover a pull request that stays open
    // across a weekend outage, and bounded because the other direction has no floor: provenance rows are
    // purged with the pull-request data they belong to, so past that point there is nothing left to attribute,
    // and an answer whose row can never be written must stop being reconsidered on every restart.
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var postedReplies = await jobRepository.GetPostedRepliesAsync(
            DateTimeOffset.UtcNow - RecoveryWindow,
            MaxRepliesPerPass,
            ct);

        if (postedReplies.Count == 0)
        {
            return 0;
        }

        var recordedJobIds = (await postedCommentOriginStore.GetJobIdsWithOriginsAsync(
                postedReplies.Select(reply => reply.JobId).ToList(),
                ct))
            .ToHashSet();

        var missing = postedReplies
            .Where(reply => !recordedJobIds.Contains(reply.JobId))
            .Select(reply => new PostedCommentOriginEntry(
                reply.ClientId,
                reply.RepositoryId,
                reply.PullRequestId,
                reply.ProviderThreadId,
                reply.ProviderCommentId,
                reply.JobId,
                reply.PostedAt))
            .ToList();

        if (missing.Count == 0)
        {
            return 0;
        }

        // Recording is idempotent on the natural key, so a row another writer added between the read above and
        // here is refreshed rather than duplicated.
        await postedCommentOriginStore.RecordAsync(missing, ct);
        LogProvenanceRecovered(logger, missing.Count, postedReplies.Count);
        return missing.Count;
    }
}
