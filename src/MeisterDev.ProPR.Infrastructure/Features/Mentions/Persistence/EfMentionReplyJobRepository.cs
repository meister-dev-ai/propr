// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Mentions.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IMentionReplyJobRepository" />.
///     Provides persistent storage for mention reply jobs backed by PostgreSQL.
/// </summary>
public sealed partial class EfMentionReplyJobRepository(
    MeisterProPRDbContext dbContext,
    IAuthorActivityRecorder? authorActivityRecorder = null,
    ILogger<EfMentionReplyJobRepository>? logger = null) : IMentionReplyJobRepository
{
    /// <inheritdoc />
    public async Task AddAsync(MentionReplyJob job, CancellationToken ct = default)
    {
        await dbContext.MentionReplyJobs.AddAsync(job, ct);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MentionReplyJob>> GetPendingAsync(CancellationToken ct = default)
    {
        return await dbContext.MentionReplyJobs
            .AsNoTracking()
            .Where(j => j.Status == MentionJobStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsForCommentAsync(
        string repositoryId,
        int pullRequestId,
        string threadId,
        long commentId,
        string mentionedReviewerKey,
        CancellationToken ct = default)
    {
        return await dbContext.MentionReplyJobs
            .AnyAsync(
                j =>
                    j.RepositoryId == repositoryId &&
                    j.PullRequestId == pullRequestId &&
                    j.ThreadId == threadId &&
                    j.CommentId == commentId &&
                    j.MentionedReviewerKey == mentionedReviewerKey,
                ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetPostedReplyCommentIdsAsync(
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var postedCommentIds = await dbContext.MentionReplyJobs
            .AsNoTracking()
            .Where(j =>
                j.RepositoryId == repositoryId &&
                j.PullRequestId == pullRequestId &&
                j.PostedReplyCommentId != null)
            .Select(j => j.PostedReplyCommentId!)
            .Distinct()
            .ToListAsync(ct);

        // Ordinal, because these are provider identifiers rather than words: two that differ in case are two
        // different comments on a provider that spells them in hexadecimal.
        return postedCommentIds.ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(MentionReplyJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await dbContext.MentionReplyJobs.AddAsync(job, ct);

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Detached whatever went wrong, not only on the race. One scan cycle shares a single context
            // across every configuration and pull request it visits, so a job left in Added state after a
            // failed write is replayed by the next SaveChangesAsync and fails it too. One over-long value
            // from a provider would otherwise take down the rest of the cycle.
            dbContext.Entry(job).State = EntityState.Detached;

            if (IsMentionUniquenessViolation(ex))
            {
                // Another client covering the same repository reached this comment first. An ordinary
                // outcome: both were right to look, and exactly one of them answers.
                return false;
            }

            throw;
        }
    }

    // Narrowed to the one constraint that means "already taken". Treating every DbUpdateException as a lost
    // race would swallow genuine write failures and report a mention as answered when nothing was stored.
    private static bool IsMentionUniquenessViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
               && string.Equals(
                   postgres.ConstraintName,
                   "uq_mention_reply_jobs_mention",
                   StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<bool> TryTransitionAsync(
        Guid jobId,
        MentionJobStatus from,
        MentionJobStatus to,
        CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null || job.Status != from)
        {
            return false;
        }

        job.Status = to;
        if (to == MentionJobStatus.Processing)
        {
            job.ProcessingStartedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Concurrency conflict occurred, another process likely updated the job. Reload the entity to get the latest state.
            await dbContext.Entry(job).ReloadAsync(ct);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task SetExecutionContextAsync(
        Guid jobId,
        int? iterationId,
        Guid? connectionId,
        string? model,
        CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null)
        {
            return;
        }

        job.SetIteration(iterationId);
        job.SetAiConfig(connectionId, model);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetBudgetHeldAsync(
        Guid jobId,
        int? iterationId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null)
        {
            return;
        }

        job.Status = MentionJobStatus.BudgetHeld;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.SetIteration(iterationId);
        job.SetBudgetBlock(scope, capKind, thresholdUsd, spentUsd);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null)
        {
            return;
        }

        job.Status = MentionJobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetCompletedAsync(Guid jobId, string? postedReplyCommentId, CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null)
        {
            return;
        }

        job.Status = MentionJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.PostedReplyCommentId = NormalizeCommentId(postedReplyCommentId);
        await dbContext.SaveChangesAsync(ct);

        await this.RecordAuthorActivityAsync(job, ct);
    }

    /// <summary>
    ///     Puts the author of the answered comment into the current month's rollup.
    /// </summary>
    /// <remarks>
    ///     Only a job carrying the host's own identifier for the asker contributes. A row written before that
    ///     column existed, and a comment whose payload named no identifier, contribute nothing: the derived
    ///     identifier beside it would not match the same person's identifier on a reviewed pull request, so
    ///     keying on it would count one person twice.
    ///     <para>
    ///         Fail-soft. Nothing in the answer reads the rollup, so a write that fails is logged and the job
    ///         stays completed. What a lost write costs is one author missing from one month, which undercounts
    ///         that month unless the same author has other work complete within it; it never blocks the answer,
    ///         which is already on the pull request by this point.
    ///     </para>
    ///     <para>
    ///         The names and the bot flag travel with the identifier, because whether the asker counts is
    ///         decided where they are recorded. The flag's column is not nullable, so a false value cannot be
    ///         told apart from a payload that stated nothing, and the name-based rules still apply to it. A
    ///         comment payload carries one name, which is stored as the login and the display name alike.
    ///     </para>
    /// </remarks>
    private async Task RecordAuthorActivityAsync(MentionReplyJob job, CancellationToken ct)
    {
        if (authorActivityRecorder is null || string.IsNullOrWhiteSpace(job.CommentAuthorNativeId))
        {
            return;
        }

        try
        {
            await authorActivityRecorder.RecordAsync(
                new AuthorActivityObservation(
                    job.ProviderHost,
                    job.CommentAuthorNativeId,
                    AuthorActivitySource.MentionAnswer,
                    job.CommentAuthorLogin,
                    job.CommentAuthorDisplayName,
                    job.CommentAuthorIsBot),
                ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger is not null)
            {
                LogAuthorActivityNotRecorded(logger, exception, job.Id);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to record the author of mention reply job {JobId} in the monthly rollup; the answer "
                  + "stands and the month may undercount this author.")]
    private static partial void LogAuthorActivityNotRecorded(ILogger logger, Exception exception, Guid jobId);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostedMentionReply>> GetPostedRepliesAsync(
        DateTimeOffset completedAtOrAfter,
        int maxResults,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);

        return await dbContext.MentionReplyJobs
            .AsNoTracking()
            .Where(j => j.Status == MentionJobStatus.Completed
                        && j.PostedReplyCommentId != null
                        && j.CompletedAt != null
                        && j.CompletedAt >= completedAtOrAfter)
            .OrderByDescending(j => j.CompletedAt)
            .Take(maxResults)
            .Select(j => new PostedMentionReply(
                j.Id,
                j.ClientId,
                j.RepositoryId,
                j.PullRequestId,
                j.ThreadId,
                j.PostedReplyCommentId!,
                j.CompletedAt!.Value))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task ResetStuckProcessingAsync(CancellationToken ct = default)
    {
        await dbContext.MentionReplyJobs
            .Where(j => j.Status == MentionJobStatus.Processing)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, MentionJobStatus.Pending)
                    .SetProperty(j => j.ProcessingStartedAt, (DateTimeOffset?)null),
                ct);
    }

    // An adapter that reported no comment id, or reported whitespace, has told us nothing to attribute. Store
    // null for both so the recovery sweep's "knows its own comment id" filter means exactly that.
    private static string? NormalizeCommentId(string? postedReplyCommentId)
    {
        return string.IsNullOrWhiteSpace(postedReplyCommentId) ? null : postedReplyCommentId.Trim();
    }
}
