// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Processes a single <see cref="MentionReplyJob" />: fetches full PR context,
///     generates an AI answer grounded in the PR, and posts it as a thread reply.
/// </summary>
public sealed partial class MentionReplyService(
    IPullRequestFetcher pullRequestFetcher,
    IMentionReplyJobRepository jobRepository,
    IMentionAnswerService answerService,
    IScmProviderRegistry providerRegistry,
    ILogger<MentionReplyService> logger,
    IProviderActivationService? providerActivationService = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null) : IMentionReplyService
{
    /// <inheritdoc />
    public async Task ProcessAsync(MentionReplyJob job, CancellationToken cancellationToken = default)
    {
        // Atomic claim: transition Pending → Processing before doing expensive work.
        var claimed = await jobRepository.TryTransitionAsync(
            job.Id,
            MentionJobStatus.Pending,
            MentionJobStatus.Processing,
            cancellationToken);

        if (!claimed)
        {
            LogJobAlreadyClaimed(logger, job.Id);
            return;
        }

        try
        {
            if (providerActivationService is not null &&
                !await providerActivationService.IsEnabledAsync(job.Provider, cancellationToken))
            {
                await jobRepository.SetFailedAsync(
                    job.Id,
                    "The provider family is currently disabled by system administration.",
                    cancellationToken);
                return;
            }

            // Fetch full PR context (iterationId = 1 is sufficient for existing threads).
            var pullRequest = await pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                1,
                null,
                job.ClientId,
                cancellationToken);

            // Generate an AI answer grounded in the PR, focused on the specific thread.
            var answer = await answerService.AnswerAsync(
                pullRequest,
                job.ClientId,
                job.MentionText,
                job.ThreadId,
                cancellationToken);

            // Post the reply to the ADO thread.
            var replyCommentId = await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                .ReplyAsync(job.ClientId, job.ReviewThreadReference, answer, cancellationToken);

            await jobRepository.SetCompletedAsync(job.Id, cancellationToken);
            LogJobCompleted(logger, job.Id);

            // Provenance last. It is bookkeeping, and nothing that can throw may sit between posting the
            // answer and completing the job: a cancellation in that gap leaves the answer on the pull request
            // and the job stuck in Processing, where nothing retries it and nothing reports it failed.
            await this.RecordPostedReplyOriginAsync(job, replyCommentId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJobFailed(logger, job.Id, ex);

            await jobRepository.SetFailedAsync(
                job.Id,
                ex.Message,
                cancellationToken);
        }
    }

    // A mention answer is a comment ProPR authored, so it carries provenance like any other. Without a row it
    // reads back as a human comment wherever no token identity is resolvable, and the thread it sits on is
    // misattributed. The mention job is the originating job, so its own id is what the row records.
    //
    // Strictly best-effort: the answer is already on the pull request by the time this runs, and a recording
    // failure must neither undo it nor fail the job. An adapter that reported no comment id records nothing.
    private async Task RecordPostedReplyOriginAsync(
        MentionReplyJob job,
        string? providerCommentId,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null || string.IsNullOrWhiteSpace(providerCommentId))
        {
            return;
        }

        try
        {
            await postedCommentOriginStore.RecordAsync(
                [
                    new PostedCommentOriginEntry(
                        job.ClientId,
                        job.RepositoryId,
                        job.PullRequestId,
                        job.ReviewThreadReference.ExternalThreadId,
                        providerCommentId,
                        job.Id,
                        DateTimeOffset.UtcNow),
                ],
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPostedCommentOriginRecordingFailed(logger, job.Id, ex);
        }
    }
}
