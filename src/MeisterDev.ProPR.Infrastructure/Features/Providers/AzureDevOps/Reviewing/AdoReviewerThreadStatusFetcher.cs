// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using Microsoft.VisualStudio.Services.WebApi;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>
///     ADO-backed implementation of <see cref="IReviewerThreadStatusFetcher" />.
///     Fetches all non-deleted threads for a PR, filters to the ones ProPR owns,
///     and builds the comment history string from all non-system comments.
/// </summary>
public sealed partial class AdoReviewerThreadStatusFetcher(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoReviewerThreadStatusFetcher> logger) : IProviderReviewerThreadStatusFetcher
{
    private const int MaxCommentHistoryLength = 8_000;

    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    internal Func<string, CancellationToken, Task<Guid?>>? AuthorizedIdentityResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    /// <inheritdoc />
    public Task<IReadOnlyList<PrThreadStatusEntry>> GetFeedbackStatusesAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default)
    {
        return this.ReadAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct, true);
    }

    public Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default)
    {
        return this.ReadAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct, false);
    }

    private async Task<IReadOnlyList<PrThreadStatusEntry>> ReadAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct, bool requireComplete)
    {
        try
        {
            return (await this.ReadSnapshotAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct)).ReviewerThreads;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!requireComplete)
        {
            LogFetchFailed(logger, pullRequestId, ex);
            return [];
        }
    }

    public async Task<PullRequestThreadSnapshot> ReadSnapshotAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default, Guid? connectionId = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ct.ThrowIfCancellationRequested();
        var (gitClient, authorizedIdentityId) = await this.ResolveGitClientAndAuthorizedIdentityAsync(organizationUrl, clientId, ct, connectionId);
        ownership.ContributeIdentity(new ThreadOwnerIdentity(authorizedIdentityId));
        var observedAt = DateTimeOffset.UtcNow;
        var rawThreads = await gitClient.GetThreadsAsync(projectId, repositoryId, pullRequestId, cancellationToken: ct)
                         ?? throw new InvalidOperationException("Azure DevOps thread lookup returned no payload.");
        return await this.ProjectSnapshotAsync(gitClient, projectId, repositoryId, pullRequestId, ownership, rawThreads, observedAt, ct);
    }

    public async Task<PullRequestThreadSnapshot> ReadTargetedSnapshotAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId, IReadOnlyList<string> threadIds,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default, Guid? connectionId = null)
    {
        ArgumentNullException.ThrowIfNull(threadIds);
        var numbers = new HashSet<int>();
        foreach (var id in threadIds)
        {
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            {
                return await this.ReadSnapshotAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct, connectionId);
            }

            numbers.Add(number);
        }

        if (numbers.Count == 0)
        {
            return await this.ReadSnapshotAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct, connectionId);
        }

        var ids = numbers.Order().Select(number => number.ToString(CultureInfo.InvariantCulture)).ToArray();
        var (gitClient, authorizedIdentityId) = await this.ResolveGitClientAndAuthorizedIdentityAsync(organizationUrl, clientId, ct, connectionId);
        ownership.ContributeIdentity(new ThreadOwnerIdentity(authorizedIdentityId));
        var observedAt = DateTimeOffset.UtcNow;
        var rawThreads = new List<GitPullRequestCommentThread>();
        foreach (var id in ids)
        {
            var number = int.Parse(id, CultureInfo.InvariantCulture);
            try
            {
                var thread = await gitClient.GetPullRequestThreadAsync(projectId, repositoryId, pullRequestId, number, cancellationToken: ct)
                             ?? throw new InvalidOperationException("Azure DevOps thread lookup returned no payload.");
                if (thread.Id != number)
                {
                    throw new InvalidOperationException("Azure DevOps returned a different discussion thread.");
                }

                rawThreads.Add(thread);
            }
            catch (VssServiceResponseException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
            {
                rawThreads.Add(new GitPullRequestCommentThread { Id = number, IsDeleted = true, Comments = [] });
            }
        }

        var snapshot = await this.ProjectSnapshotAsync(gitClient, projectId, repositoryId, pullRequestId, ownership, rawThreads, observedAt, ct);
        return snapshot with { EnumerationComplete = false, TargetThreadId = ids.Length == 1 ? ids[0] : null, TargetThreadIds = ids };
    }

    private async Task<PullRequestThreadSnapshot> ProjectSnapshotAsync(
        GitHttpClient gitClient, string projectId, string repositoryId,
        int pullRequestId, ThreadOwnershipResolver ownership, IReadOnlyList<GitPullRequestCommentThread> rawThreads,
        DateTimeOffset observedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new List<PrThreadStatusEntry>();
        var iterationChanges = new IterationChangeCache();

        foreach (var thread in rawThreads)
        {
            if (thread.IsDeleted || thread.Comments is null || thread.Comments.Count == 0)
            {
                continue;
            }

            // Keep only threads ProPR owns, decided by the first comment that still exists.
            var firstComment = thread.Comments.FirstOrDefault(c => !c.IsDeleted);
            if (firstComment is null)
            {
                continue;
            }

            var threadId = thread.Id.ToString(CultureInfo.InvariantCulture);
            if (!ownership.OwnsThread(ToCommentRef(threadId, firstComment)))
            {
                continue;
            }

            var commentHistory = BuildCommentHistory(thread);
            var nonReviewerReplyCount = thread.Comments.Count(c =>
                !c.IsDeleted &&
                !string.Equals(c.CommentType.ToString(), "system", StringComparison.OrdinalIgnoreCase) &&
                !ownership.OwnsComment(ToCommentRef(threadId, c)));

            var codeChange = await this.ResolveAnchorCodeChangeAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                thread,
                iterationChanges,
                ct);

            results.Add(
                new PrThreadStatusEntry(
                    thread.Id.ToString(CultureInfo.InvariantCulture),
                    thread.Status.ToString(),
                    thread.ThreadContext?.FilePath,
                    commentHistory,
                    nonReviewerReplyCount,
                    codeChange));
        }


        var byId = results.ToDictionary(thread => thread.ThreadId!, StringComparer.Ordinal);
        var threads = new List<PrCommentThread>(rawThreads.Count);
        foreach (var thread in rawThreads)
        {
            var anchor = await this.ResolveConcernAnchorAsync(gitClient, projectId, repositoryId, pullRequestId, thread, iterationChanges, ct);
            threads.Add(
                new PrCommentThread(
                    thread.Id.ToString(CultureInfo.InvariantCulture),
                    thread.ThreadContext?.FilePath, thread.ThreadContext?.RightFileStart?.Line,
                    (thread.Comments ?? []).Select(comment => AdoPrFetcher.ToThreadComment(comment) with
                    {
                        IsDeleted = comment.IsDeleted,
                        LastUpdatedAt = comment.LastUpdatedDate != default ? new DateTimeOffset(comment.LastUpdatedDate, TimeSpan.Zero) : null,
                    }).ToArray(), thread.Status.ToString(), thread.IsDeleted,
                    JsonSerializer.Serialize(new { thread.ThreadContext, thread.PullRequestThreadContext }),
                    byId.TryGetValue(thread.Id.ToString(CultureInfo.InvariantCulture), out var status)
                        ? status.CodeChangedSinceRaised
                        : ThreadAnchorCodeChange.Unknown,
                    thread.LastUpdatedDate != default ? new DateTimeOffset(thread.LastUpdatedDate, TimeSpan.Zero) : null, anchor));
        }

        return new PullRequestThreadSnapshot(observedAt, threads, results.AsReadOnly()) { AuthenticatedIdentity = ownership.Identity };
    }

    private async Task<ReviewConcernAnchor> ResolveConcernAnchorAsync(
        GitHttpClient gitClient, string projectId, string repositoryId, int pullRequestId,
        GitPullRequestCommentThread thread, IterationChangeCache cache, CancellationToken ct)
    {
        var root = thread.Comments?.FirstOrDefault(comment => comment.CommentType != CommentType.System);
        var originalContext = thread.PullRequestThreadContext?.IterationContext;
        var tracking = thread.PullRequestThreadContext?.TrackingCriteria;
        var tracked = tracking is not null && (tracking.FirstComparingIteration is > 0 || tracking.SecondComparingIteration is > 0);
        var rightStart = tracked ? tracking?.OrigRightFileStart : thread.ThreadContext?.RightFileStart;
        var rightEnd = tracked ? tracking?.OrigRightFileEnd : thread.ThreadContext?.RightFileEnd;
        var leftStart = tracked ? tracking?.OrigLeftFileStart : thread.ThreadContext?.LeftFileStart;
        var leftEnd = tracked ? tracking?.OrigLeftFileEnd : thread.ThreadContext?.LeftFileEnd;
        var hasRight = rightStart?.Line is > 0;
        var hasLeft = leftStart?.Line is > 0;
        var side = hasRight && !hasLeft ? "RIGHT" : hasLeft && !hasRight ? "LEFT" : null;
        var first = originalContext?.FirstComparingIteration;
        var second = originalContext?.SecondComparingIteration;
        var anchor = new ReviewConcernAnchor
        {
            RootCommentId = root is { Id: > 0 } ? root.Id.ToString(CultureInfo.InvariantCulture) : null,
            OriginAt = root is not null && root.PublishedDate != default ? new DateTimeOffset(root.PublishedDate, TimeSpan.Zero) : null,
            FilePath = tracked ? tracking?.OrigFilePath : thread.ThreadContext?.FilePath, Side = side,
            StartLine = side == "RIGHT" ? rightStart?.Line : side == "LEFT" ? leftStart?.Line : null,
            EndLine = side == "RIGHT" ? rightEnd?.Line ?? rightStart?.Line : side == "LEFT" ? leftEnd?.Line ?? leftStart?.Line : null,
            NativeRevisionId = second is > 0 ? second.Value.ToString(CultureInfo.InvariantCulture) : null,
        };
        if (root is null || root.IsDeleted || second is not > 0 || side is null)
        {
            return anchor;
        }

        try
        {
            var current = await cache.GetIterationAsync(gitClient, projectId, repositoryId, pullRequestId, second.Value, ct);
            var previous = first is > 0 && first != second
                ? await cache.GetIterationAsync(gitClient, projectId, repositoryId, pullRequestId, first.Value, ct)
                : null;
            var head = current?.SourceRefCommit?.CommitId;
            var basis = first == second ? current?.CommonRefCommit?.CommitId : previous?.SourceRefCommit?.CommitId;
            var commit = side == "RIGHT" ? head : basis;
            var known = anchor with { HeadSha = head, BaseSha = basis, CommitSha = commit, OriginalScopeProven = !string.IsNullOrWhiteSpace(commit) };
            return known.IsKnown ? known : known with { OriginalScopeProven = false };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                "Original concern anchor unavailable for PR {PullRequestId}, thread {ThreadId}: {FailureType}, code {FailureCode}",
                pullRequestId, thread.Id, exception.GetType().Name, exception.HResult);
            return anchor;
        }
    }

    private async Task<(GitHttpClient GitClient, Guid? AuthorizedIdentityId)>
        ResolveGitClientAndAuthorizedIdentityAsync(
            string organizationUrl,
            Guid clientId,
            CancellationToken ct, Guid? connectionId = null)
    {
        if (this.GitClientResolver is not null)
        {
            var authorizedIdentityId = this.AuthorizedIdentityResolver is not null
                ? await this.AuthorizedIdentityResolver(organizationUrl, ct)
                : null;

            return (await this.GitClientResolver(organizationUrl, ct), authorizedIdentityId);
        }

        var credentials = connectionId.HasValue
            ? AdoProviderAdapterHelpers.ToAdoCredentials(await connectionRepository.GetOperationalConnectionByIdAsync(clientId, connectionId.Value, ct))
            : await AdoProviderAdapterHelpers.ResolveCredentialsAsync(connectionRepository, clientId, organizationUrl, ct);
        if (connectionId.HasValue && credentials is null)
        {
            throw new InvalidOperationException("The captured Azure DevOps connection is unavailable.");
        }

        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        await connection.ConnectAsync(ct);
        return (await connection.GetClientAsync<GitHttpClient>(ct), connection.AuthorizedIdentity?.Id);
    }

    // Resolves whether the code a resolved thread is anchored to changed after the finding was raised,
    // by comparing the iteration the thread was left on against the latest iteration's changed files.
    // Any failure or missing context is reported as Unknown so a claimed fix is never trusted on a guess.
    private async Task<ThreadAnchorCodeChange> ResolveAnchorCodeChangeAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        int pullRequestId,
        GitPullRequestCommentThread thread,
        IterationChangeCache iterationChanges,
        CancellationToken ct)
    {
        if (!IsResolvedThreadStatus(thread.Status))
        {
            return ThreadAnchorCodeChange.Unknown;
        }

        var filePath = thread.ThreadContext?.FilePath;
        var threadIteration = thread.PullRequestThreadContext?.IterationContext?.SecondComparingIteration;
        if (string.IsNullOrWhiteSpace(filePath) || threadIteration is null)
        {
            return ThreadAnchorCodeChange.Unknown;
        }

        try
        {
            var latestIteration = await iterationChanges.GetLatestIterationIdAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                ct);
            if (latestIteration is null)
            {
                return ThreadAnchorCodeChange.Unknown;
            }

            if (threadIteration.Value >= latestIteration.Value)
            {
                // The thread is anchored to the latest iteration; nothing was pushed after it.
                return ThreadAnchorCodeChange.Unchanged;
            }

            var changedPaths = await iterationChanges.GetChangedPathsAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                threadIteration.Value,
                latestIteration.Value,
                ct);
            if (changedPaths is null)
            {
                return ThreadAnchorCodeChange.Unknown;
            }

            if (changedPaths.Paths.Contains(filePath))
            {
                return ThreadAnchorCodeChange.Changed;
            }

            // The anchored file is absent from the change set. Only trust "unchanged" when the set is
            // complete; a truncated listing cannot rule the file out, so it stays undetermined.
            return changedPaths.Complete ? ThreadAnchorCodeChange.Unchanged : ThreadAnchorCodeChange.Unknown;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCodeChangeResolutionFailed(logger, pullRequestId, thread.Id, ex);
            return ThreadAnchorCodeChange.Unknown;
        }
    }

    private static bool IsResolvedThreadStatus(CommentThreadStatus status)
    {
        return status is CommentThreadStatus.Fixed or CommentThreadStatus.Closed
            or CommentThreadStatus.WontFix or CommentThreadStatus.ByDesign;
    }

    // Azure DevOps scopes a comment id to its thread, so both ids travel together: the pair is what the
    // provenance rows were recorded under. The author display name is left out because the authenticated
    // identity is a GUID here, so a name could only ever match by coincidence.
    private static ThreadCommentRef ToCommentRef(string threadId, Comment comment)
    {
        return new ThreadCommentRef(
            threadId,
            comment.Id.ToString(CultureInfo.InvariantCulture),
            Guid.TryParse(comment.Author?.Id, out var authorId) ? authorId : null);
    }

    private static string BuildCommentHistory(GitPullRequestCommentThread thread)
    {
        var lines = thread.Comments!
            .Where(c => !c.IsDeleted && !string.Equals(
                c.CommentType.ToString(),
                "system",
                StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Author?.DisplayName ?? "Unknown"}: {ReviewFindingTextFormatter.CompactBadgeHeaderForHistory(c.Content ?? "")}");

        var result = string.Join("\n", lines);

        if (result.Length > MaxCommentHistoryLength)
        {
            result = result[..MaxCommentHistoryLength];
        }

        return result;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to fetch reviewer thread statuses for PR #{PullRequestId}. Returning empty list.")]
    private static partial void LogFetchFailed(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Could not resolve anchor code-change signal for PR #{PullRequestId} thread {ThreadId}; treating as undetermined.")]
    private static partial void LogCodeChangeResolutionFailed(ILogger logger, int pullRequestId, int threadId, Exception ex);

    // Per-request cache for pull-request iteration data so repeated threads share the same lookups.
    private sealed class IterationChangeCache
    {
        private const int ChangePageSize = 2000;

        private readonly Dictionary<int, IterationChangeSet?> _changesByFromIteration = new();
        private Dictionary<int, GitPullRequestIteration> _iterations = [];
        private int? _latestIterationId;
        private bool _latestResolved;

        public async Task<int?> GetLatestIterationIdAsync(
            GitHttpClient gitClient,
            string projectId,
            string repositoryId,
            int pullRequestId,
            CancellationToken ct)
        {
            if (this._latestResolved)
            {
                return this._latestIterationId;
            }

            this._latestResolved = true;
            var iterations = await gitClient.GetPullRequestIterationsAsync(
                projectId,
                repositoryId,
                pullRequestId,
                cancellationToken: ct);
            this._iterations = (iterations ?? []).Where(item => item.Id is > 0).GroupBy(item => item.Id!.Value)
                .ToDictionary(group => group.Key, group => group.First());
            var maxId = iterations?
                .Where(iteration => iteration.Id.HasValue)
                .Select(iteration => iteration.Id!.Value)
                .DefaultIfEmpty(0)
                .Max() ?? 0;
            this._latestIterationId = maxId > 0 ? maxId : null;
            return this._latestIterationId;
        }

        public async Task<GitPullRequestIteration?> GetIterationAsync(
            GitHttpClient gitClient, string projectId, string repositoryId, int pullRequestId, int iterationId, CancellationToken ct)
        {
            _ = await this.GetLatestIterationIdAsync(gitClient, projectId, repositoryId, pullRequestId, ct);
            return this._iterations.GetValueOrDefault(iterationId);
        }

        public async Task<IterationChangeSet?> GetChangedPathsAsync(
            GitHttpClient gitClient,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int fromIteration,
            int toIteration,
            CancellationToken ct)
        {
            if (this._changesByFromIteration.TryGetValue(fromIteration, out var cached))
            {
                return cached;
            }

            var changes = await gitClient.GetPullRequestIterationChangesAsync(
                projectId,
                repositoryId,
                pullRequestId,
                toIteration,
                ChangePageSize,
                compareTo: fromIteration,
                cancellationToken: ct);

            IterationChangeSet? result = null;
            if (changes?.ChangeEntries is not null)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entryCount = 0;
                foreach (var change in changes.ChangeEntries)
                {
                    entryCount++;
                    var path = change?.Item?.Path;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }

                result = new IterationChangeSet(paths, entryCount < ChangePageSize);
            }

            this._changesByFromIteration[fromIteration] = result;
            return result;
        }
    }

    private sealed record IterationChangeSet(HashSet<string> Paths, bool Complete);
}
