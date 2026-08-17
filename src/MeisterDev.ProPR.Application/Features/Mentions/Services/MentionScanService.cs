// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Threading.Channels;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Orchestrates a single mention scan cycle across all active mention configurations.
///     Discovers pull requests in the repositories each configuration claims, detects mentions of the
///     client's reviewer identity in their comment threads, and enqueues <see cref="MentionReplyJob" />
///     items for processing.
/// </summary>
public sealed partial class MentionScanService(
    IMentionConfigurationRepository mentionConfigs,
    IActivePrFetcher activePrFetcher,
    IPullRequestFetcher pullRequestFetcher,
    IClientRegistry clientRegistry,
    IMentionScanRepository scanRepository,
    IMentionReplyJobRepository jobRepository,
    ChannelWriter<MentionReplyJob> channelWriter,
    ILogger<MentionScanService> logger,
    IProviderActivationService? providerActivationService = null,
    IScmProviderRegistry? providerRegistry = null) : IMentionScanService
{
    // Default look-back window for the first scan when no watermark exists.
    private static readonly TimeSpan InitialLookBack = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        var configs = await mentionConfigs.GetAllActiveAsync(cancellationToken);
        LogScanCycleStarted(logger, configs.Count);

        foreach (var config in configs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (providerActivationService is not null &&
                !await providerActivationService.IsEnabledAsync(config.Provider, cancellationToken))
            {
                // Logged, because a configuration whose provider an administrator disabled answers nothing
                // and previously did so with no log entry, which looks the same as a scan that is not running.
                LogConfigSkippedProviderDisabled(logger, config.Id, config.Provider);
                continue;
            }

            await this.ScanConfigAsync(config, cancellationToken);
        }
    }

    private async Task ScanConfigAsync(MentionConfigurationDto config, CancellationToken ct)
    {
        try
        {
            var reviewer = await this.ResolveReviewerIdentityAsync(config, ct);
            if (reviewer is null)
            {
                LogSkippedNoReviewerId(logger, config.Id, config.ClientId);
                return;
            }

            var projectScan = await scanRepository.GetProjectScanAsync(config.Id, ct);

            // The configuration's own interval, measured from the last time this configuration was scanned.
            // The worker ticks on one shared cadence, so without this a configuration asking to be scanned
            // rarely would still be scanned on every tick.
            if (projectScan is not null
                && DateTimeOffset.UtcNow - projectScan.LastScannedAt
                < TimeSpan.FromSeconds(Math.Max(1, config.ScanIntervalSeconds)))
            {
                LogConfigNotDueYet(logger, config.Id, projectScan.LastScannedAt, config.ScanIntervalSeconds);
                return;
            }

            // Where the last scan that read everything got to, not where the last scan got to. A tick that a
            // throttle or an unreadable repository left partial does not move this, so the window it failed
            // to cover is asked about again. A row written before that was recorded carries its old value.
            var updatedAfter = projectScan?.LastCompleteScanAt
                               ?? projectScan?.LastScannedAt
                               ?? DateTimeOffset.UtcNow.Subtract(InitialLookBack);

            var claimedAtByRepository = config.RepoFilters
                .GroupBy(filter => filter.RepositoryId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Min(filter => filter.ClaimedAt ?? DateTimeOffset.UtcNow),
                    StringComparer.OrdinalIgnoreCase);

            // Stamped before anything is asked, and used as the new watermark below rather than the time the
            // tick finished. A comment posted while the tick is running would otherwise fall between the
            // listing that missed it and a watermark set after it arrived, and never be asked for again.
            var startedAt = DateTimeOffset.UtcNow;

            var discovery = await activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                new ActivePullRequestQuery(
                    config.Provider,
                    config.ProviderScopePath,
                    config.RepoFilters
                        .Select(filter => new ClaimedRepositoryRef(filter.RepositoryId, filter.DisplayName))
                        .ToList(),
                    updatedAfter,
                    config.ClientId),
                ct);

            LogPrsFound(
                logger,
                config.ProviderScopePath,
                config.ProviderProjectKey,
                discovery.PullRequests.Count);

            // Discovery is only asked about claimed repositories, so this filter normally removes nothing.
            // It stays as a second check: a client reading the conversations of a repository it never claimed
            // cannot be undone, and an adapter returning an extra repository would cause exactly that.
            var coveredPrs = discovery.PullRequests
                .Where(pr => claimedAtByRepository.ContainsKey(pr.RepositoryId))
                .ToList();

            LogPrsAfterRepositoryFilter(
                logger,
                config.Id,
                coveredPrs.Count,
                discovery.PullRequests.Count);

            var readEverything = discovery.IsComplete;

            foreach (var pr in coveredPrs)
            {
                if (ct.IsCancellationRequested)
                {
                    readEverything = false;
                    break;
                }

                if (!await this.ScanPrAsync(
                        config,
                        reviewer,
                        pr.RepositoryId,
                        pr.PullRequestId,
                        pr.LastUpdatedAt,
                        claimedAtByRepository[pr.RepositoryId],
                        ct))
                {
                    readEverything = false;
                }
            }

            var updatedProjectScan = projectScan ?? new MentionProjectScan(
                Guid.NewGuid(),
                config.Id,
                DateTimeOffset.UtcNow);

            // The configuration was scanned whatever came of it, so its interval advances and the next tick
            // does not scan it again immediately.
            updatedProjectScan.LastScannedAt = DateTimeOffset.UtcNow;

            // The window only closes over ground actually covered. A throttle, a repository that has gone, or
            // a pull request that could not be read leaves it open, so the next tick asks about it again
            // instead of stepping over a question nobody has seen.
            if (readEverything)
            {
                updatedProjectScan.LastCompleteScanAt = startedAt;
            }
            else
            {
                LogScanWindowHeldOpen(logger, config.Id, updatedAfter);
            }

            await scanRepository.UpsertProjectScanAsync(updatedProjectScan, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogConfigScanError(logger, config.Id, ex);
        }
    }

    /// <summary>
    ///     Scans one pull request, and reports whether everything it holds was read.
    /// </summary>
    /// <remarks>
    ///     The answer decides whether the configuration's discovery window may close over this pull request.
    ///     A read that failed leaves questions unseen, and a window closed over them would never bring them
    ///     back: discovery asks for what changed since the watermark, and a pull request nobody touches again
    ///     never changes.
    /// </remarks>
    private async Task<bool> ScanPrAsync(
        MentionConfigurationDto config,
        ReviewerIdentity reviewer,
        string repositoryId,
        int pullRequestId,
        DateTimeOffset prLastUpdatedAt,
        DateTimeOffset claimedAt,
        CancellationToken ct)
    {
        var prScan = await scanRepository.GetPrScanAsync(config.Id, repositoryId, pullRequestId, ct);

        // Nothing has happened here since the last scan, so there was nothing to read and everything was read.
        if (prScan is not null && prLastUpdatedAt <= prScan.LastCommentSeenAt)
        {
            LogPrSkippedNoNewActivity(logger, pullRequestId);
            return true;
        }

        // Fetch the full PR with thread context (iterationId = 1 is sufficient for comment scanning).
        PullRequest pullRequest;
        try
        {
            pullRequest = await pullRequestFetcher.FetchAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                repositoryId,
                pullRequestId,
                1,
                null,
                config.ClientId,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPrFetchError(logger, pullRequestId, ex);
            return false;
        }

        // Questions are asked in the pull request's conversation as well as on lines of code. Azure DevOps
        // returns both from the thread listing above. The other providers keep them apart, and they are read
        // separately here so they stay out of PullRequest.ExistingThreads, which the review prompt, the file
        // reviewer and the thread pass read expecting threads with a file position.
        var conversation = await this.FetchConversationThreadsAsync(config, repositoryId, pullRequestId, ct);
        var threads = conversation.Threads.Count == 0
            ? pullRequest.ExistingThreads ?? []
            : [.. pullRequest.ExistingThreads ?? [], .. conversation.Threads];

        var latestCommentTimestamp = ComputeLatestCommentTimestamp(
            threads,
            prScan?.LastCommentSeenAt ?? DateTimeOffset.MinValue);

        // Read once for the pull request rather than per comment: this is what tells ProPR's own answers from
        // the questions it is looking for, and without it an answer repeating the reviewer's handle is read as
        // a new question on the next scan, answered, and read again.
        var ownAnswers = await jobRepository.GetPostedReplyCommentIdsAsync(repositoryId, pullRequestId, ct);
        var newMentionsEnqueued = 0;

        foreach (var thread in threads)
        {
            foreach (var comment in thread.Comments)
            {
                if (await this.ProcessCommentForMentionAsync(
                        new MentionCommentInputs(
                            config,
                            reviewer,
                            repositoryId,
                            pullRequestId,
                            pullRequest,
                            thread,
                            comment,
                            prScan,
                            claimedAt,
                            ownAnswers,
                            ct)))
                {
                    newMentionsEnqueued++;
                }
            }
        }

        // Only over what was read. The watermark is a floor on how old an answerable comment may be, so
        // advancing it past a conversation that could not be listed refuses the questions in it from then on:
        // they are older than the floor on every later scan, and the pull request is skipped outright once
        // its last update is older too. Leaving it where it was costs a re-read and nothing else.
        if (conversation.Complete)
        {
            var updatedPrScan = prScan ?? new MentionPrScan(
                Guid.NewGuid(),
                config.Id,
                repositoryId,
                pullRequestId,
                latestCommentTimestamp);

            if (latestCommentTimestamp > DateTimeOffset.MinValue)
            {
                updatedPrScan.LastCommentSeenAt = latestCommentTimestamp;
            }

            await scanRepository.UpsertPrScanAsync(updatedPrScan, ct);
        }

        if (newMentionsEnqueued == 0)
        {
            LogPrScanCompletedNoMentions(logger, pullRequestId);
        }
        else
        {
            LogPrScanCompletedWithMentions(logger, pullRequestId, newMentionsEnqueued);
        }

        return conversation.Complete;
    }

    /// <summary>
    ///     Reads the pull request's own conversation, where the provider keeps it apart from review threads,
    ///     and reports whether the read succeeded.
    /// </summary>
    /// <remarks>
    ///     A failure costs this pull request's conversation and nothing else: the review threads have already
    ///     been read, and answering a question on a line of code is worth more than failing the whole scan
    ///     because the timeline could not be listed. It is reported rather than swallowed because the caller
    ///     moves a watermark afterwards, and an empty list from a failed read is indistinguishable from a
    ///     pull request whose conversation is genuinely empty.
    /// </remarks>
    private async Task<ConversationRead> FetchConversationThreadsAsync(
        MentionConfigurationDto config,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct)
    {
        try
        {
            var threads = await pullRequestFetcher.FetchConversationThreadsAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                repositoryId,
                pullRequestId,
                config.ClientId,
                ct);

            return new ConversationRead(threads, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogConversationFetchError(logger, pullRequestId, ex);
            return new ConversationRead([], false);
        }
    }

    private static DateTimeOffset ComputeLatestCommentTimestamp(
        IReadOnlyList<PrCommentThread> threads,
        DateTimeOffset seed)
    {
        // Advance the watermark to the latest comment we've seen, but never below the previous watermark:
        // the seed is always in the running set, so the result can only move forward (monotonic).
        return threads
            .SelectMany(thread => thread.Comments)
            .Select(comment => comment.PublishedAt)
            .Where(publishedAt => publishedAt.HasValue)
            .Select(publishedAt => publishedAt!.Value)
            .Append(seed)
            .Max();
    }

    /// <summary>
    ///     The moment a comment has to fall after to be answerable: the later of the claim and the watermark.
    /// </summary>
    /// <remarks>
    ///     The claim time covers a repository never scanned before: the provider hands back every open pull
    ///     request whatever its age, so an absent watermark treated as "process everything" would answer, and
    ///     bill for, every question the repository has ever been asked.
    ///     Using the later of the two also covers a repository removed from a configuration and added back
    ///     later. Its scan rows survive the gap, so the older watermark would win and every question asked
    ///     while the repository was unclaimed would be answered. Claiming a repository applies from the moment
    ///     of the claim.
    /// </remarks>
    private static DateTimeOffset ComputeSeenUpTo(MentionPrScan? prScan, DateTimeOffset claimedAt)
    {
        return prScan is null
            ? claimedAt
            : prScan.LastCommentSeenAt > claimedAt
                ? prScan.LastCommentSeenAt
                : claimedAt;
    }

    private static bool ShouldProcessComment(
        PrThreadComment comment,
        MentionPrScan? prScan,
        DateTimeOffset claimedAt)
    {
        // Skip comments without a valid ID (shouldn't happen with real ADO data).
        if (comment.CommentId <= 0)
        {
            return false;
        }

        // A comment the provider does not date cannot be shown to fall after either floor, and treating it
        // as new defeats both of them: the first scan after a repository is claimed would answer, and bill
        // for, every undated question the repository has ever been asked. The cost of being wrong the other
        // way is that a provider omitting timestamps answers nothing, which is visible and recoverable,
        // where a spent budget is neither.
        if (!comment.PublishedAt.HasValue)
        {
            return false;
        }

        // Skip comments we've already processed (published before or at last seen time).
        return comment.PublishedAt.Value > ComputeSeenUpTo(prScan, claimedAt);
    }

    // Produce a redacted, single-line, length-bounded rendering of (attacker-controlled) comment text
    // so Trace diagnostics can still spot format changes without leaking full content or letting a
    // crafted comment inject extra log lines via embedded control characters.
    private static string SanitizeCommentForLog(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        const int maxLoggedChars = 256;
        var trimmed = content.Length > maxLoggedChars ? content[..maxLoggedChars] + "…" : content;
        return new string(trimmed.Select(static ch => char.IsControl(ch) ? ' ' : ch).ToArray());
    }

    private async Task<bool> ProcessCommentForMentionAsync(MentionCommentInputs inputs)
    {
        if (!ShouldProcessComment(inputs.Comment, inputs.PrScan, inputs.ClaimedAt))
        {
            // A question that names the reviewer and is turned away for its age is the one skip an operator
            // comes looking for, and it used to leave no trace at all: claiming a repository takes effect
            // from that moment, so a question asked before it is never answered and nothing said so.
            if (MentionDetector.IsMentioned(inputs.Comment.Content, inputs.Reviewer))
            {
                LogMentionOlderThanFloor(
                    logger,
                    inputs.PullRequestId,
                    inputs.Comment.CommentId,
                    inputs.Comment.PublishedAt,
                    ComputeSeenUpTo(inputs.PrScan, inputs.ClaimedAt));
            }

            return false;
        }

        // An answer ProPR posted is not a question, whatever it says. Quoting covers the part of it that
        // repeats the question, and on a provider that replies inside the thread there is no quote at all, so
        // an answer that names the reviewer in its own words would otherwise be answered in turn, for as long
        // as the pull request stayed open. Keyed on what was posted rather than on who posted it, because an
        // installation whose reviewer identity is an account a person also posts from would lose every real
        // question to an author check.
        if (inputs.OwnAnswers.Contains(inputs.Comment.CommentId.ToString(CultureInfo.InvariantCulture)))
        {
            LogOwnAnswerSkipped(logger, inputs.PullRequestId, inputs.Comment.CommentId);
            return false;
        }

        var threadKey = this.ResolveThreadKey(inputs.Config.Provider, inputs.Thread, inputs.Comment);
        if (threadKey is null)
        {
            // Logged only when the comment mentions the reviewer. A provider that names no thread names
            // none for any comment, so logging every one would produce noise without information.
            if (MentionDetector.IsMentioned(inputs.Comment.Content, inputs.Reviewer))
            {
                LogMentionWithoutAnswerableThread(
                    logger,
                    inputs.PullRequestId,
                    inputs.Comment.CommentId,
                    inputs.Config.Provider);
            }

            return false;
        }

        // Log a redacted, single-line, length-bounded rendering of the content so we can still detect
        // format changes without leaking full (attacker-controlled) comment text or allowing log injection.
        LogCommentContent(logger, threadKey, inputs.Comment.CommentId, SanitizeCommentForLog(inputs.Comment.Content));

        if (!MentionDetector.IsMentioned(inputs.Comment.Content, inputs.Reviewer))
        {
            return false;
        }

        // Asked across every client, because another client covering this repository may already have taken
        // the comment. The unique constraint is the authoritative guard; this only avoids building a job
        // that would lose.
        var alreadyTaken = await jobRepository.ExistsForCommentAsync(
            inputs.RepositoryId,
            inputs.PullRequestId,
            threadKey,
            inputs.Comment.CommentId,
            inputs.Reviewer.AddressedKey,
            inputs.Ct);

        if (alreadyTaken)
        {
            LogDuplicateMentionSkipped(logger, inputs.PullRequestId, threadKey, inputs.Comment.CommentId);
            return false;
        }

        var job = new MentionReplyJob(
            Guid.NewGuid(),
            inputs.Config.ClientId,
            inputs.Config.ProviderScopePath,
            inputs.Config.ProviderProjectKey,
            inputs.RepositoryId,
            inputs.PullRequestId,
            threadKey,
            inputs.Comment.CommentId,
            inputs.Comment.Content,
            inputs.Thread.FilePath,
            inputs.Thread.LineNumber,
            inputs.Comment.AuthorId,
            inputs.Comment.AuthorName,
            inputs.Comment.PublishedAt);

        var host = new ProviderHostRef(inputs.Config.Provider, inputs.Config.ProviderScopePath);
        var repository = new RepositoryRef(
            host,
            inputs.RepositoryId,
            inputs.Config.ProviderProjectKey,
            ResolveRepositoryProjectPath(inputs.Config, inputs.RepositoryId, inputs.PullRequest));
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            inputs.PullRequestId.ToString(),
            inputs.PullRequestId);
        var threadRef = new ReviewThreadRef(
            review,
            threadKey,
            inputs.Thread.FilePath,
            inputs.Thread.LineNumber,
            false);
        var commentAuthorExternalUserId = inputs.Comment.AuthorId?.ToString("D") ?? inputs.Comment.AuthorName;
        var commentRef = new ReviewCommentRef(
            threadRef,
            inputs.Comment.CommentId.ToString(),
            new ReviewerIdentity(
                host,
                commentAuthorExternalUserId ?? inputs.Reviewer.ExternalUserId,
                inputs.Comment.AuthorName,
                inputs.Comment.AuthorName,
                false),
            inputs.Comment.PublishedAt);

        job.SetProviderReviewContext(review);
        job.SetReviewThreadContext(threadRef);
        job.SetReviewCommentContext(commentRef);

        // The account the developer addressed, which is what makes this comment one unit of work rather
        // than one per client that happens to cover the repository.
        job.SetMentionedReviewer(inputs.Reviewer);

        // Another client's scan can take the comment between the check above and this write, so the answer
        // that matters is the one the database gives.
        if (!await jobRepository.TryAddAsync(job, inputs.Ct))
        {
            LogMentionTakenByAnotherClient(
                logger,
                inputs.PullRequestId,
                threadKey,
                inputs.Comment.CommentId);
            return false;
        }

        await channelWriter.WriteAsync(job, inputs.Ct);
        LogMentionEnqueued(logger, inputs.PullRequestId, threadKey, inputs.Comment.CommentId);
        return true;
    }

    /// <summary>
    ///     The identifier the answer is addressed through and the duplicate guard is keyed on, or
    ///     <see langword="null" /> when this comment cannot be answered at all.
    /// </summary>
    /// <remarks>
    ///     A thread's own identifier when it has one. Forgejo has none for a comment on a line of code: it
    ///     exposes no thread object there, and its adapter reports the absence rather than handing back
    ///     something that resolves to a comment. Its reply publisher does not need one — it answers on the pull
    ///     request and says which comment it answers with a quote — so the comment's own identifier serves as
    ///     the key, and a question asked on a line of code is answered like any other.
    ///     A provider whose publisher does address a thread gets no such substitute: a job built on an
    ///     identifier it cannot post into would spend an answer and then fail to publish it. Absent registry,
    ///     absent publisher, and a publisher that needs a thread all read the same way, so this never widens
    ///     what is accepted because something is missing.
    /// </remarks>
    private string? ResolveThreadKey(ScmProvider provider, PrCommentThread thread, PrThreadComment comment)
    {
        if (!string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            return thread.ThreadId;
        }

        if (providerRegistry is null || providerRegistry.RequiresReviewThreadIdentifier(provider))
        {
            return null;
        }

        return comment.CommentId.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<ReviewerIdentity?> ResolveReviewerIdentityAsync(
        MentionConfigurationDto config,
        CancellationToken ct)
    {
        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
        return await clientRegistry.GetEffectiveReviewerIdentityAsync(config.ClientId, host, ct);
    }

    /// <summary>
    ///     Names the repository the way its provider writes it, for the reference the reply is addressed
    ///     through.
    /// </summary>
    /// <remarks>
    ///     Guided selection stores a repository by the provider's own id, so joining the scope to that id
    ///     builds something shaped like an owner and a name that names no repository. The name recorded
    ///     beside the id when it was claimed is what carries the real pair, and it is preferred over anything
    ///     assembled here.
    /// </remarks>
    private static string ResolveRepositoryProjectPath(
        MentionConfigurationDto config,
        string repositoryId,
        PullRequest pullRequest)
    {
        if (config.Provider == ScmProvider.AzureDevOps)
        {
            return config.ProviderProjectKey;
        }

        var claimedName = config.RepoFilters
            .FirstOrDefault(filter =>
                string.Equals(filter.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName;

        if (LooksLikeOwnerAndName(claimedName))
        {
            return claimedName!.Trim();
        }

        if (LooksLikeOwnerAndName(pullRequest.RepositoryName))
        {
            return pullRequest.RepositoryName;
        }

        // Nothing here names the pair, so the identifier stands alone rather than being dressed up as a path.
        // What addresses the provider resolves it from this identifier anyway.
        return repositoryId;
    }

    private static bool LooksLikeOwnerAndName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;
    }

    /// <summary>What a conversation read returned, and whether it returned it because there was nothing.</summary>
    private sealed record ConversationRead(IReadOnlyList<PrCommentThread> Threads, bool Complete);

    private sealed record MentionCommentInputs(
        MentionConfigurationDto Config,
        ReviewerIdentity Reviewer,
        string RepositoryId,
        int PullRequestId,
        PullRequest PullRequest,
        PrCommentThread Thread,
        PrThreadComment Comment,
        MentionPrScan? PrScan,
        DateTimeOffset ClaimedAt,
        IReadOnlySet<string> OwnAnswers,
        CancellationToken Ct);
}
