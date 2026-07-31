// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.History;
using MeisterDev.ProPR.CodeInsights.Ports;

namespace MeisterDev.ProPR.CodeInsights.History;

/// <summary>
///     Replays historical reviews into the collection by rebuilding the events the live path raises.
/// </summary>
/// <remarks>
///     <para>
///         Nothing here derives a fact of its own. Findings come from the review's persisted file results, the
///         thread a posted comment belongs to comes from recorded provenance, the anchor that ties a thread to a
///         finding comes from the retained thread, and every write goes through the same consumer the live path
///         uses. A second way of producing collection rows would be a second thing to keep correct.
///     </para>
///     <para>
///         Jobs the collection already holds findings for are skipped rather than merged. A finding is identified
///         by its position in the job that produced it, and a replay cannot promise the same ordering the live
///         capture used, so replaying over an already-captured job could record its findings twice.
///     </para>
/// </remarks>
public sealed partial class CodeInsightHistoryImporter(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ICodeInsightFindingIngestionService ingestionService,
    ILogger<CodeInsightHistoryImporter> logger,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IReviewArchiveStore? reviewArchiveStore = null,
    ICodeInsightDispositionService? dispositionService = null,
    ICodeInsightMissHarvester? missHarvester = null,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightHistoryImporter
{
    /// <summary>
    ///     What the aggregate records as the pull-request state when no retained pull request names one. The
    ///     collection's own seal sweeper closes an idle aggregate out later, so an import does not have to invent
    ///     a lifecycle it cannot observe.
    /// </summary>
    private const string UnknownPullRequestState = "Unknown";

    public async Task<CodeInsightImportResult> ImportAsync(
        CodeInsightImportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await gate.IsCollectionEnabledAsync(request.ClientId, ct))
        {
            // A closed gate is the answer, not an error: an unlicensed or opted-out client has nothing to import
            // into, and saying so lets the caller show why rather than reporting an empty run.
            LogGated(logger, request.ClientId);
            return CodeInsightImportResult.Gated;
        }

        var maxJobs = Math.Clamp(request.MaxJobs, 1, CodeInsightImportRequest.MaxJobsCeiling);

        // One more than the bound, so "there is more to do" is something the run observed rather than inferred
        // from having filled its own quota. A window holding exactly the bound is finished, and says so.
        var read = await this.LoadJobsAsync(request, maxJobs + 1, ct);
        var reachedLimit = read.Count > maxJobs;
        var jobs = reachedLimit ? read.Take(maxJobs).ToList() : read;

        if (jobs.Count == 0)
        {
            return new CodeInsightImportResult(0, 0, 0, 0, 0, 0, 0, 0, false);
        }

        var collectedCounts = await this.LoadCollectedFindingCountsAsync(jobs, ct);
        var pending = jobs.Where(job => !collectedCounts.ContainsKey(job.JobId)).ToList();

        var findingsImported = 0;
        var findingsWithoutThread = 0;
        var jobsImported = 0;
        var outcomeThreads = 0;
        var humanThreads = 0;
        var pullRequests = new HashSet<(string RepositoryId, long PullRequestId)>();

        var pendingJobIds = pending.Select(job => job.JobId).ToHashSet();
        var unreplayableThreads = 0;

        // Grouped by pull request because the provenance and retained-thread reads are per pull request: a pull
        // request reviewed ten times reads them once rather than ten times. Grouped over every job read rather
        // than only the ones still to import, because asking for outcomes after a findings-only run is the
        // expected way to use this: those jobs are already collected, and their threads have still never been read.
        foreach (var group in jobs.GroupBy(job => (job.RepositoryId, job.PullRequestId)))
        {
            var groupHasPendingJobs = group.Any(job => pendingJobIds.Contains(job.JobId));

            // Nothing to import here and no outcomes asked for, so the three reads those would need are not made.
            // A repeated findings-only run over a covered window costs its job query and nothing else.
            if (!groupHasPendingJobs && !request.IncludeOutcomes)
            {
                continue;
            }

            var anchors = await this.LoadThreadAnchorsAsync(request.ClientId, group.Key.RepositoryId, group.Key.PullRequestId, ct);
            var touched = false;

            foreach (var job in group.OrderBy(job => job.SubmittedAt).ThenBy(job => job.JobId))
            {
                if (!pendingJobIds.Contains(job.JobId))
                {
                    continue;
                }

                var comments = await this.LoadFindingsAsync(job.JobId, ct);
                if (comments.Count == 0)
                {
                    continue;
                }

                var produced = BuildProducedFindings(comments, anchors.ByAnchor(job.JobId));
                var evt = new ReviewFindingsProducedEvent(
                    request.ClientId,
                    job.RepositoryId,
                    job.PullRequestId,
                    job.JobId,
                    ReviewRevisionKeys.GetStoredKey(job.Revision, job.IterationId),
                    anchors.PullRequestState ?? UnknownPullRequestState,
                    job.ObservedAt,
                    produced,
                    job.RepositoryName);

                await ingestionService.HandleReviewFindingsProducedAsync(evt, ct);

                jobsImported++;
                findingsImported += produced.Count;
                findingsWithoutThread += produced.Count(finding => finding.ProviderThreadId is null);
                touched = true;
            }

            if (request.IncludeOutcomes)
            {
                var (outcomes, humans, unreplayable) = await this.ReplayThreadsAsync(
                    request.ClientId,
                    group.Key.RepositoryId,
                    group.Key.PullRequestId,
                    anchors,
                    ct);
                outcomeThreads += outcomes;
                humanThreads += humans;
                unreplayableThreads += unreplayable;
                touched |= outcomes > 0 || humans > 0;
            }

            // Counted once the group actually had something written for it, whether that was findings or outcomes:
            // an outcomes-only run touches pull requests it imported no findings for.
            if (touched)
            {
                pullRequests.Add(group.Key);
            }
        }

        LogImported(logger, request.ClientId, jobsImported, findingsImported, outcomeThreads, humanThreads);

        return new CodeInsightImportResult(
            jobs.Count,
            jobsImported,
            jobs.Count - pending.Count,
            findingsImported,
            findingsWithoutThread,
            pullRequests.Count,
            outcomeThreads,
            humanThreads,
            false,
            reachedLimit,
            collectedCounts.Values.Sum(),
            unreplayableThreads);
    }

    /// <summary>
    ///     Rebuilds the per-finding payload the live path builds, including the ordinal that gives a finding its
    ///     identity. The ordering is the file path then the finding's position inside that file's result, which is
    ///     deterministic so a re-run of the same job produces the same ordinals.
    /// </summary>
    private static IReadOnlyList<ReviewFindingProduced> BuildProducedFindings(
        IReadOnlyList<ReviewComment> comments,
        IReadOnlyDictionary<(string? FilePath, int? Line), Queue<ThreadAnchor>> anchorsByPosition)
    {
        var produced = new List<ReviewFindingProduced>(comments.Count);

        for (var ordinal = 0; ordinal < comments.Count; ordinal++)
        {
            var comment = comments[ordinal];

            // One thread per finding, taken in order from the threads recorded at that position. Claiming matters:
            // two findings sharing a thread would have one thread's resolution recorded as two outcomes.
            ThreadAnchor? anchor = null;
            if (anchorsByPosition.TryGetValue((comment.FilePath, comment.LineNumber), out var queue)
                && queue.Count > 0)
            {
                anchor = queue.Dequeue();
            }

            produced.Add(
                new ReviewFindingProduced(
                    ordinal,
                    comment.FilePath,
                    comment.LineNumber,
                    comment.Severity,
                    comment.Message,
                    comment.OriginPassKind,
                    comment.OriginPassIndex,
                    comment.OriginPassLens,
                    comment.OriginPassShadow,
                    comment.ScopeRelation,
                    comment.SourceReadGrounding,
                    anchor?.ThreadId,
                    anchor?.CommentId,
                    comment.OriginModelId,
                    comment.OriginLogicalModelName,
                    comment.OriginSymbolName,
                    comment.OriginSymbolKind));
        }

        return produced;
    }

    private async Task<List<JobRow>> LoadJobsAsync(
        CodeInsightImportRequest request,
        int maxJobs,
        CancellationToken ct)
    {
        var from = request.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = request.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return await this.WithDbAsync(
            async db => await db.ReviewJobs
                .AsNoTracking()
                .Where(job => job.ClientId == request.ClientId
                              && job.Status == JobStatus.Completed
                              && job.SubmittedAt >= from
                              && job.SubmittedAt < to)
                // Oldest first, so repeated runs advance through the window instead of re-reading its newest edge.
                .OrderBy(job => job.SubmittedAt)
                .ThenBy(job => job.Id)
                .Take(maxJobs)
                .Select(job => new JobRow(
                    job.Id,
                    job.RepositoryId,
                    job.PullRequestId,
                    job.IterationId,
                    job.SubmittedAt,
                    job.CompletedAt,
                    job.PrRepositoryName,
                    job.RevisionHeadSha,
                    job.RevisionBaseSha,
                    job.RevisionStartSha,
                    job.ProviderRevisionId,
                    job.ReviewPatchIdentity))
                .ToListAsync(ct),
            ct);
    }

    /// <summary>
    ///     How many findings the collection already holds per job, for the jobs in this window. A job present here
    ///     is one this run leaves alone, and the totals are reported so the sum of what was imported and what was
    ///     already held can be compared against the findings coverage says those reviews produced. A job collected
    ///     only in part cannot be repaired by importing over it, since identity is a finding's position, so the
    ///     discrepancy is made visible instead of being hidden behind a job count.
    /// </summary>
    private async Task<Dictionary<Guid, int>> LoadCollectedFindingCountsAsync(
        IReadOnlyList<JobRow> jobs,
        CancellationToken ct)
    {
        var jobIds = jobs.Select(job => job.JobId).ToList();

        var collected = await this.WithDbAsync(
            async db => await db.CodeInsightFindings
                .AsNoTracking()
                .Where(finding => jobIds.Contains(finding.JobId))
                .GroupBy(finding => finding.JobId)
                .Select(group => new { JobId = group.Key, Count = group.Count() })
                .ToListAsync(ct),
            ct);

        return collected.ToDictionary(row => row.JobId, row => row.Count);
    }

    /// <summary>
    ///     The findings a job actually produced. Carried-forward results are excluded, exactly as synthesis
    ///     excludes them when it assembles what a review publishes: a carried-forward result holds a copy of an
    ///     earlier job's findings, which that earlier job is imported for. Counting them again would collect
    ///     findings the live path deliberately never collects, and would give one problem a second chain.
    /// </summary>
    private async Task<List<ReviewComment>> LoadFindingsAsync(Guid jobId, CancellationToken ct)
    {
        var results = await this.WithDbAsync(
            async db => await db.ReviewFileResults
                .AsNoTracking()
                .Where(result => result.JobId == jobId
                                 && result.Comments != null
                                 && !result.IsCarriedForward)
                .OrderBy(result => result.FilePath)
                .ThenBy(result => result.Id)
                .Select(result => result.Comments!)
                .ToListAsync(ct),
            ct);

        return results.SelectMany(comments => comments).ToList();
    }

    /// <summary>
    ///     Resolves which provider thread each of this pull request's posted comments belongs to, and where that
    ///     thread is anchored. Provenance names the thread; the retained thread supplies the file and line, which
    ///     is the only thing that ties a thread back to the finding it was posted for. Where threads were never
    ///     retained there is no anchor, and the findings import without one.
    /// </summary>
    private async Task<ThreadAnchors> LoadThreadAnchorsAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct)
    {
        if (reviewArchiveStore is null)
        {
            return ThreadAnchors.None;
        }

        var threads = await reviewArchiveStore.GetThreadsForPullRequestAsync(clientId, repositoryId, pullRequestId, ct);
        if (threads.Count == 0)
        {
            return ThreadAnchors.None;
        }

        // Provenance decides which of these threads are ProPR's own, and nothing more. Its absence leaves every
        // thread looking like somebody else's, which is the right answer for the harvester and costs only the
        // outcomes that were never linkable anyway: a miss is a thread ProPR did not post, and establishing that
        // needs no record of what it did post.
        var provenance = postedCommentOriginStore is null
            ? []
            : await postedCommentOriginStore.GetJobIdsForPullRequestAsync(clientId, repositoryId, pullRequestId, ct);

        var byThreadId = threads.ToDictionary(thread => thread.ThreadId, StringComparer.Ordinal);
        var anchors = new List<(Guid JobId, ThreadAnchor Anchor)>();

        foreach (var row in provenance)
        {
            if (row.ProviderThreadId is not { } threadId
                || !byThreadId.TryGetValue(threadId, out var thread)
                || thread.FilePath is null)
            {
                continue;
            }

            anchors.Add((row.JobId, new ThreadAnchor(threadId, row.ProviderCommentId, thread.FilePath, thread.Line)));
        }

        var ourThreadIds = anchors.Select(entry => entry.Anchor.ThreadId).ToHashSet(StringComparer.Ordinal);
        var state = await this.ResolvePullRequestStateAsync(clientId, repositoryId, pullRequestId, ct);
        return new ThreadAnchors(anchors, threads, ourThreadIds, state);
    }

    /// <summary>
    ///     Replays what became of this pull request's threads. Threads ProPR posted a finding as go to the outcome
    ///     path; everything else goes to the harvester, which decides for itself whether it was a miss. Both are
    ///     the live consumers, so both apply their own gate, their own idempotence and their own model bounds.
    /// </summary>
    private async Task<(int Outcomes, int Human, int Unreplayable)> ReplayThreadsAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        ThreadAnchors anchors,
        CancellationToken ct)
    {
        var outcomes = 0;
        var humans = 0;
        var unreplayable = 0;

        foreach (var thread in anchors.Threads)
        {
            if (anchors.OurThreadIds.Contains(thread.ThreadId))
            {
                if (dispositionService is null || !IsResolved(thread.Status))
                {
                    continue;
                }

                // The outcome path keys on a numeric provider thread id, as the live crawl does. A provider whose
                // thread ids are not numeric is left alone rather than matched against a fabricated id.
                if (!long.TryParse(thread.ThreadId, out var numericThreadId))
                {
                    // Counted rather than dropped in silence, so a provider whose thread ids are not numeric shows
                    // up as an explained zero instead of an unexplained one.
                    unreplayable++;
                    continue;
                }

                await dispositionService.HandleThreadResolvedAsync(
                    new ThreadResolvedDomainEvent(
                        clientId,
                        repositoryId,
                        (int)pullRequestId,
                        numericThreadId,
                        thread.FilePath,
                        null,
                        BuildCommentHistory(thread),
                        thread.UpdatedAt,
                        ThreadResolutionStatusInterpreter.InterpretIntent(thread.Status),
                        // Whether the code moved after the finding was raised is not something a replay can see,
                        // and Unknown is what the outcome mapper is built to receive when nobody observed it.
                        ThreadAnchorCodeChange.Unknown),
                    ct);

                outcomes++;
                continue;
            }

            if (missHarvester is null)
            {
                continue;
            }

            await missHarvester.HandleThreadObservedAsync(
                new ThreadUpdatedEvent(
                    clientId,
                    Guid.Empty,
                    repositoryId,
                    pullRequestId,
                    thread.ThreadId,
                    thread.FilePath,
                    thread.Line,
                    thread.Status,
                    thread.UpdatedAt,
                    thread.Comments
                        .Select(comment => new ThreadUpdatedComment(
                            comment.CommentId,
                            comment.AuthorIdentity,
                            comment.IsAiAuthored,
                            comment.PublishedAt,
                            comment.Text))
                        .ToList()),
                ct);

            humans++;
        }

        return (outcomes, humans, unreplayable);
    }

    private static string BuildCommentHistory(RetainedThreadView thread)
    {
        return string.Join(
            '\n',
            thread.Comments.Select(comment => $"{comment.AuthorIdentity}: {comment.Text}"));
    }

    private static bool IsResolved(string? status)
    {
        return ThreadResolutionStatusInterpreter.IsResolved(ThreadResolutionStatusInterpreter.InterpretIntent(status));
    }

    /// <summary>
    ///     The pull request's last known state as retention recorded it. Absent when nothing was retained for it,
    ///     in which case the aggregate records that the state is unknown and the collection's own seal sweeper
    ///     closes it out once it has been idle long enough. Nothing here infers a lifecycle it cannot observe.
    /// </summary>
    private async Task<string?> ResolvePullRequestStateAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct)
    {
        var state = await this.WithDbAsync(
            async db => await db.RetainedPullRequests
                .AsNoTracking()
                .Where(pullRequest => pullRequest.ClientId == clientId
                                      && pullRequest.RepositoryId == repositoryId
                                      && pullRequest.PullRequestId == pullRequestId)
                .OrderByDescending(pullRequest => pullRequest.LastActivityAt)
                .Select(pullRequest => pullRequest.PrState)
                .FirstOrDefaultAsync(ct),
            ct);

        return string.IsNullOrWhiteSpace(state) ? null : state;
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }

    /// <summary>One historical review job, with the columns the revision key is derived from.</summary>
    private readonly record struct JobRow(
        Guid JobId,
        string RepositoryId,
        int PullRequestId,
        int IterationId,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? CompletedAt,
        string? RepositoryName,
        string? HeadSha,
        string? BaseSha,
        string? StartSha,
        string? ProviderRevisionId,
        string? PatchIdentity)
    {
        /// <summary>When the findings were observed: the job's own completion, falling back to its submission.</summary>
        public DateTimeOffset ObservedAt => this.CompletedAt ?? this.SubmittedAt;

        /// <summary>The revision as the review pipeline expresses it, or null when the job recorded no revision.</summary>
        public ReviewRevision? Revision => string.IsNullOrWhiteSpace(this.HeadSha) || string.IsNullOrWhiteSpace(this.BaseSha)
            ? null
            : new ReviewRevision(this.HeadSha, this.BaseSha, this.StartSha, this.ProviderRevisionId, this.PatchIdentity);
    }

    /// <summary>A provider thread ProPR posted a finding as, and where that thread is anchored.</summary>
    private readonly record struct ThreadAnchor(string ThreadId, string CommentId, string FilePath, int? Line);

    /// <summary>Every anchor for one pull request, with the retained threads they were resolved from.</summary>
    private sealed record ThreadAnchors(
        IReadOnlyList<(Guid JobId, ThreadAnchor Anchor)> Anchors,
        IReadOnlyList<RetainedThreadView> Threads,
        HashSet<string> OurThreadIds,
        string? PullRequestState)
    {
        public static ThreadAnchors None { get; } = new([], [], [], null);

        /// <summary>This job's anchors, keyed by the position a finding would carry.</summary>
        public IReadOnlyDictionary<(string? FilePath, int? Line), Queue<ThreadAnchor>> ByAnchor(Guid jobId)
        {
            var byPosition = new Dictionary<(string? FilePath, int? Line), Queue<ThreadAnchor>>();
            foreach (var (anchorJobId, anchor) in this.Anchors)
            {
                if (anchorJobId != jobId)
                {
                    continue;
                }

                // Every thread at a position, in order, rather than the first one: two findings can sit on one
                // line, each posted as its own thread, and keeping only one would leave the second finding
                // permanently unresolvable.
                if (!byPosition.TryGetValue((anchor.FilePath, anchor.Line), out var queue))
                {
                    queue = new Queue<ThreadAnchor>();
                    byPosition[(anchor.FilePath, anchor.Line)] = queue;
                }

                queue.Enqueue(anchor);
            }

            return byPosition;
        }
    }
}
