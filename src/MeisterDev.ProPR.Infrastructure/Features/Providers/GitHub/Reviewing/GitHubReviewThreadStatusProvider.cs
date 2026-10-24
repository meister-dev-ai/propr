// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewThreadStatusProvider(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IProviderReviewerThreadStatusFetcher
{
    // Read across pages from pageInfo, as the fetcher's copy of this query is. The comment connection inside
    // each thread is still one page, so a thread past a hundred comments contributes its first hundred.
    private const string GitHubReviewThreadsQuery =
        "query ReviewThreads($owner: String!, $name: String!, $pullRequestNumber: Int!, $after: String) { repository(owner: $owner, name: $name) { pullRequest(number: $pullRequestNumber) { reviewThreads(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { id isResolved isOutdated path line originalLine originalStartLine diffSide startDiffSide subjectType comments(first: 100) { pageInfo { hasNextPage endCursor } nodes { databaseId body createdAt path originalLine originalStartLine originalCommit { oid } publishedAt replyTo { databaseId } author { login ... on User { databaseId } ... on Bot { databaseId } } } } } } } } }";

    public ScmProvider Provider => ScmProvider.GitHub;

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
        return (await this.ReadSnapshotCoreAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct, requireComplete))
            .ReviewerThreads;
    }

    public Task<PullRequestThreadSnapshot> ReadSnapshotAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default, Guid? connectionId = null)
    {
        return this.ReadSnapshotCoreAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct, true, connectionId);
    }

    private async Task<PullRequestThreadSnapshot> ReadSnapshotCoreAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct, bool requireComplete, Guid? connectionId = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        var host = new ProviderHostRef(ScmProvider.GitHub, organizationUrl);
        var context = connectionId.HasValue
            ? await connectionVerifier.VerifyAsync(new ConnectionDiscoveryContext(clientId, connectionId.Value, host), ct)
            : await connectionVerifier.VerifyAsync(clientId, host, ct);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repositoryId, ct);
        var observedAt = DateTimeOffset.UtcNow;
        var threads = await this.GetReviewThreadsAsync(context, host, repositoryPath, pullRequestId, ct, requireComplete);

        // GitHub names an author by login, and the connection verification above is the only place the
        // authenticated one exists, so the pass's resolver is completed with it here. It is contributed into
        // the instance the caller handed down, so the consumers that run later in the same pass, and never see
        // a connection of their own, decide with the identity too.
        ownership.ContributeIdentity(new ThreadOwnerIdentity(Login: context.AuthenticatedActorLogin));

        var statuses = threads
            .Where(thread => thread.Comments.Nodes.Count > 0)
            .Where(thread => ownership.OwnsThread(ToCommentRef(thread.Comments.Nodes[0])))
            .Select(thread => new PrThreadStatusEntry(
                thread.Id,
                thread.IsResolved == true ? "Fixed" : "Active",
                thread.Path,
                BuildCommentHistory(thread.Comments.Nodes),
                thread.Comments.Nodes.Count(comment => !ownership.OwnsComment(ToCommentRef(comment))),
                // GitHub's isOutdated only means the thread's diff hunk no longer maps onto the current
                // diff: it is also set by rebases and unrelated churn, and never confirms the flagged
                // concern was addressed. Treat an outdated thread as undetermined rather than a corroborated
                // code change, so a claimed fix is not trusted as grounded without stronger evidence. A
                // thread that is still current genuinely has an unchanged anchor.
                thread.IsOutdated == true || (requireComplete && thread.IsOutdated is null)
                    ? ThreadAnchorCodeChange.Unknown
                    : ThreadAnchorCodeChange.Unchanged))
            .ToList()
            .AsReadOnly();
        var raw = threads.Select(thread => new PrCommentThread(
            thread.Id, thread.Path, thread.Line,
            thread.Comments.Nodes.Select(comment => new PrThreadComment(
                comment.Author?.Login ?? "Unknown", comment.Body ?? "",
                ProviderSnapshotCommentIdentity.FromLogin(comment.Author?.Login), comment.DatabaseId ?? 0, comment.CreatedAt,
                AuthorNativeId: comment.Author?.DatabaseId?.ToString(CultureInfo.InvariantCulture))).ToArray(),
            thread.IsResolved == true ? "Fixed" : "Active", SourceAnchor: JsonSerializer.Serialize(new { thread.Path, thread.Line, thread.IsOutdated }),
            CodeChangedSinceRaised: thread.IsOutdated == false ? ThreadAnchorCodeChange.Unchanged : ThreadAnchorCodeChange.Unknown,
            ConcernAnchor: OriginalConcernAnchor(thread))).ToArray();
        return new PullRequestThreadSnapshot(observedAt, raw, statuses) { AuthenticatedIdentity = ownership.Identity };
    }

    private static ReviewConcernAnchor OriginalConcernAnchor(GitHubReviewThreadNode thread)
    {
        var root = thread.Comments.Nodes.FirstOrDefault(comment => comment.ReplyTo is null);
        var head = root?.OriginalCommit?.Oid;
        var end = root?.OriginalLine ?? thread.OriginalLine;
        var side = thread.DiffSide;
        var anchor = new ReviewConcernAnchor
        {
            RootCommentId = root is { DatabaseId: > 0 } ? root.DatabaseId.Value.ToString(CultureInfo.InvariantCulture) : null,
            OriginAt = root?.PublishedAt, FilePath = root?.Path, Side = side,
            StartLine = root?.OriginalStartLine ?? thread.OriginalStartLine ?? end, EndLine = end,
            HeadSha = head, CommitSha = side == "RIGHT" ? head : null, NativeRevisionId = side == "RIGHT" ? head : null,
            OriginalScopeProven = side == "RIGHT" && thread.SubjectType == "LINE"
                                                  && (thread.StartDiffSide is null || thread.StartDiffSide == side) && !string.IsNullOrWhiteSpace(head),
        };
        return anchor.IsKnown ? anchor : anchor with { OriginalScopeProven = false };
    }

    private async Task<string> ResolveRepositoryPathAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken ct)
    {
        if (LooksLikeRepositoryPath(repositoryId))
        {
            return NormalizeRepositoryPath(repositoryId);
        }

        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("GitHub repository lookup failed because the repository could not be found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub repository lookup returned an empty payload.");
        if (string.IsNullOrWhiteSpace(payload.FullName))
        {
            throw new InvalidOperationException("GitHub repository lookup did not return a repository full name.");
        }

        return payload.FullName.Trim();
    }

    private static bool LooksLikeRepositoryPath(string repositoryId)
    {
        return !string.IsNullOrWhiteSpace(repositoryId)
               && repositoryId.Contains('/', StringComparison.Ordinal)
               && repositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;
    }

    private static string NormalizeRepositoryPath(string repositoryId)
    {
        return string.Join(
            '/',
            repositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task<IReadOnlyList<GitHubReviewThreadNode>> GetReviewThreadsAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct, bool requireComplete = false)
    {
        var parts = repositoryPath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("GitHub repository lookup returned an invalid repository path.");
        }

        return await ProviderCursorPager.LoadAllAsync(
            (cursor, pageCt) => this.GetReviewThreadPageAsync(
                context,
                host,
                parts[0],
                parts[1],
                pullRequestId,
                cursor,
                pageCt, requireComplete),
            $"GitHub's review-thread listing for pull request {pullRequestId}",
            ct);
    }

    private async Task<ProviderCursorPager.CursorPage<GitHubReviewThreadNode>> GetReviewThreadPageAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string owner,
        string name,
        int pullRequestId,
        string? after,
        CancellationToken ct, bool requireComplete = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubConnectionVerifier.BuildGraphQlUri(host))
        {
            Content = JsonContent.Create(
                new
                {
                    query = GitHubReviewThreadsQuery,
                    variables = new
                    {
                        owner,
                        name,
                        pullRequestNumber = pullRequestId,
                        after,
                    },
                }),
        };
        await context.AuthorizeRequestAsync(request, ct);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub review thread lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubGraphQlResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub review thread lookup returned an empty payload.");
        var connection = payload.Data?.Repository?.PullRequest?.ReviewThreads;
        if (requireComplete && payload.Errors is { Count: > 0 })
        {
            throw new InvalidOperationException("GitHub feedback lookup returned GraphQL errors.");
        }

        if (requireComplete && (connection?.Nodes is null || connection.PageInfo is null
                                                          || connection.Nodes.Any(thread =>
                                                              thread.IsResolved is null || string.IsNullOrWhiteSpace(thread.Id) ||
                                                              thread.Comments?.Nodes is null || thread.Comments.PageInfo is null
                                                              || thread.Comments.PageInfo.HasNextPage ||
                                                              thread.Comments.Nodes.Any(comment => comment.DatabaseId is null or <= 0))))
        {
            throw new CodeInsightIncompleteEnumerationException();
        }

        return new ProviderCursorPager.CursorPage<GitHubReviewThreadNode>(
            connection?.Nodes ?? [],
            connection?.PageInfo?.HasNextPage ?? false,
            connection?.PageInfo?.EndCursor);
    }

    // A GitHub comment id is unique within the pull request, so provenance resolves on it alone. The thread
    // id recorded at publish time is the review id, which this query does not return and does not need.
    private static ThreadCommentRef ToCommentRef(GitHubReviewCommentNode comment)
    {
        return new ThreadCommentRef(
            null,
            comment.DatabaseId?.ToString(CultureInfo.InvariantCulture),
            AuthorLogin: comment.Author?.Login);
    }

    private static string BuildCommentHistory(IReadOnlyList<GitHubReviewCommentNode> comments)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < comments.Count; index++)
        {
            var comment = comments[index];
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(comment.Author?.Login ?? "Unknown");
            builder.Append(": ");
            builder.Append(ReviewFindingTextFormatter.CompactBadgeHeaderForHistory(comment.Body ?? string.Empty));
        }

        return builder.ToString();
    }

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record GitHubGraphQlResponse(
        [property: JsonPropertyName("data")] GitHubGraphQlData? Data,
        [property: JsonPropertyName("errors")] IReadOnlyList<JsonElement>? Errors = null);

    private sealed record GitHubGraphQlData(
        [property: JsonPropertyName("repository")]
        GitHubGraphQlRepository? Repository);

    private sealed record GitHubGraphQlRepository(
        [property: JsonPropertyName("pullRequest")]
        GitHubGraphQlPullRequest? PullRequest);

    private sealed record GitHubGraphQlPullRequest(
        [property: JsonPropertyName("reviewThreads")]
        GitHubReviewThreadsConnection? ReviewThreads);

    private sealed record GitHubReviewThreadsConnection(
        [property: JsonPropertyName("nodes")] IReadOnlyList<GitHubReviewThreadNode>? Nodes,
        [property: JsonPropertyName("pageInfo")]
        GitHubPageInfo? PageInfo);

    private sealed record GitHubPageInfo(
        [property: JsonPropertyName("hasNextPage")]
        bool HasNextPage,
        [property: JsonPropertyName("endCursor")]
        string? EndCursor);

    private sealed record GitHubReviewThreadNode(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("isResolved")]
        bool? IsResolved,
        [property: JsonPropertyName("isOutdated")]
        bool? IsOutdated,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("line")] int? Line,
        [property: JsonPropertyName("comments")]
        GitHubReviewCommentsConnection Comments,
        [property: JsonPropertyName("originalLine")]
        int? OriginalLine = null,
        [property: JsonPropertyName("originalStartLine")]
        int? OriginalStartLine = null,
        [property: JsonPropertyName("diffSide")]
        string? DiffSide = null,
        [property: JsonPropertyName("startDiffSide")]
        string? StartDiffSide = null,
        [property: JsonPropertyName("subjectType")]
        string? SubjectType = null);

    private sealed record GitHubReviewCommentsConnection(
        [property: JsonPropertyName("nodes")] IReadOnlyList<GitHubReviewCommentNode> Nodes,
        [property: JsonPropertyName("pageInfo")]
        GitHubPageInfo? PageInfo = null);

    private sealed record GitHubReviewCommentNode(
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("createdAt")]
        DateTimeOffset CreatedAt,
        [property: JsonPropertyName("author")] GitHubActorNode? Author,
        [property: JsonPropertyName("path")] string? Path = null,
        [property: JsonPropertyName("originalLine")]
        int? OriginalLine = null,
        [property: JsonPropertyName("originalStartLine")]
        int? OriginalStartLine = null,
        [property: JsonPropertyName("originalCommit")]
        GitHubCommitNode? OriginalCommit = null,
        [property: JsonPropertyName("publishedAt")]
        DateTimeOffset? PublishedAt = null,
        [property: JsonPropertyName("replyTo")]
        GitHubCommentReference? ReplyTo = null);

    private sealed record GitHubCommitNode([property: JsonPropertyName("oid")] string? Oid);

    private sealed record GitHubCommentReference(
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId);

    private sealed record GitHubActorNode(
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId = null);
}
