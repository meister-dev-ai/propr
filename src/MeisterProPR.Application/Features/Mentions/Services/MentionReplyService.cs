// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

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
    IProviderActivationService? providerActivationService = null) : IMentionReplyService
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
            await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                .ReplyAsync(job.ClientId, job.ReviewThreadReference, answer, cancellationToken);

            await jobRepository.SetCompletedAsync(job.Id, cancellationToken);
            LogJobCompleted(logger, job.Id);
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
}
