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

                results.Add(
                    new PrThreadStatusEntry(
                        thread.Id,
                        thread.Status.ToString(),
                        thread.ThreadContext?.FilePath,
                        commentHistory,
                        nonReviewerReplyCount));
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
        return (connection.GetClient<GitHttpClient>(), connection.AuthorizedIdentity?.Id);
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
}
