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
    IAdoThreadReplier threadReplier,
    ILogger<MentionReplyService> logger) : IMentionReplyService
{
    /// <inheritdoc />
    public async Task ProcessAsync(MentionReplyJob job, CancellationToken cancellationToken = default)
    {
        // Atomic claim: transition Pending → Processing before doing expensive work.
        var claimed = await jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing, cancellationToken);

        if (!claimed)
        {
            LogJobAlreadyClaimed(logger, job.Id);
            return;
        }

        try
        {
            // Fetch full PR context (iterationId = 1 is sufficient for existing threads).
            var pullRequest = await pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                1,
                job.ClientId,
                cancellationToken);

            // Generate an AI answer grounded in the PR, focused on the specific thread.
            var answer = await answerService.AnswerAsync(pullRequest, job.MentionText, job.ThreadId, cancellationToken);

            // Post the reply to the ADO thread.
            await threadReplier.ReplyAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.ThreadId,
                answer,
                job.ClientId,
                cancellationToken);

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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MentionReplyService: job {JobId} was already claimed by another worker — skipping")]
    private static partial void LogJobAlreadyClaimed(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionReplyService: job {JobId} completed successfully")]
    private static partial void LogJobCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionReplyService: job {JobId} failed")]
    private static partial void LogJobFailed(ILogger logger, Guid jobId, Exception ex);
}
