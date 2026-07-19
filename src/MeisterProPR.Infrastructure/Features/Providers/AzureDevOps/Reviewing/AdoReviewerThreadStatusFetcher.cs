// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>
///     ADO-backed implementation of <see cref="IReviewerThreadStatusFetcher" />.
///     Fetches all non-deleted threads for a PR, filters to reviewer-owned ones,
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
    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid clientId,
        CancellationToken ct = default)
    {
        try
        {
            var (gitClient, authorizedIdentityId) = await this.ResolveGitClientAndAuthorizedIdentityAsync(
                organizationUrl,
                clientId,
                ct);

            var rawThreads = await gitClient.GetThreadsAsync(
                projectId,
                repositoryId,
                pullRequestId,
                cancellationToken: ct);

            if (rawThreads is null)
            {
                return [];
            }

            var results = new List<PrThreadStatusEntry>();
            var iterationChanges = new IterationChangeCache();

            foreach (var thread in rawThreads)
            {
                if (thread.IsDeleted || thread.Comments is null || thread.Comments.Count == 0)
                {
                    continue;
                }

                // Keep only threads whose first non-deleted comment was posted by a reviewer-owned identity.
                var firstComment = thread.Comments.FirstOrDefault(c => !c.IsDeleted);
                if (firstComment is null)
                {
                    continue;
                }

                if (!IsReviewerOwnedAuthor(firstComment.Author?.Id, reviewerId, authorizedIdentityId))
                {
                    continue;
                }

                var commentHistory = BuildCommentHistory(thread);
                var nonReviewerReplyCount = thread.Comments.Count(c =>
                    !c.IsDeleted &&
                    !string.Equals(c.CommentType.ToString(), "system", StringComparison.OrdinalIgnoreCase) &&
                    !IsReviewerOwnedAuthor(c.Author?.Id, reviewerId, authorizedIdentityId));

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
                        thread.Id,
                        thread.Status.ToString(),
                        thread.ThreadContext?.FilePath,
                        commentHistory,
                        nonReviewerReplyCount,
                        codeChange));
            }

            return results.AsReadOnly();
        }
        catch (Exception ex)
        {
            LogFetchFailed(logger, pullRequestId, ex);
            return [];
        }
    }

    private async Task<(GitHttpClient GitClient, Guid? AuthorizedIdentityId)>
        ResolveGitClientAndAuthorizedIdentityAsync(
            string organizationUrl,
            Guid clientId,
            CancellationToken ct)
    {
        if (this.GitClientResolver is not null)
        {
            var authorizedIdentityId = this.AuthorizedIdentityResolver is not null
                ? await this.AuthorizedIdentityResolver(organizationUrl, ct)
                : null;

            return (await this.GitClientResolver(organizationUrl, ct), authorizedIdentityId);
        }

        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            ct);
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

    private static bool IsReviewerOwnedAuthor(string? authorId, Guid reviewerId, Guid? authorizedIdentityId)
    {
        if (!Guid.TryParse(authorId, out var parsedAuthorId))
        {
            return false;
        }

        return parsedAuthorId == reviewerId ||
               (authorizedIdentityId.HasValue && parsedAuthorId == authorizedIdentityId.Value);
    }

    private static string BuildCommentHistory(GitPullRequestCommentThread thread)
    {
        var lines = thread.Comments!
            .Where(c => !c.IsDeleted && !string.Equals(
                c.CommentType.ToString(),
                "system",
                StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Author?.DisplayName ?? "Unknown"}: {c.Content ?? ""}");

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
        private bool _latestResolved;
        private int? _latestIterationId;

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
            var maxId = iterations?
                .Where(iteration => iteration.Id.HasValue)
                .Select(iteration => iteration.Id!.Value)
                .DefaultIfEmpty(0)
                .Max() ?? 0;
            this._latestIterationId = maxId > 0 ? maxId : null;
            return this._latestIterationId;
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
                top: ChangePageSize,
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
