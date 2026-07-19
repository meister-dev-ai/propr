// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewThreadStatusProvider(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IProviderReviewerThreadStatusFetcher
{
    private const string GitHubReviewThreadsQuery =
        "query ReviewThreads($owner: String!, $name: String!, $pullRequestNumber: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $pullRequestNumber) { reviewThreads(first: 100) { nodes { isResolved isOutdated path line comments(first: 100) { nodes { databaseId body createdAt author { login } } } } } } } }";

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid clientId,
        CancellationToken ct = default)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, organizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repositoryId, ct);
        var threads = await this.GetReviewThreadsAsync(context, host, repositoryPath, pullRequestId, ct);
        var authoredLogin = context.AuthenticatedActorLogin;

        return threads
            .Where(thread => thread.Comments.Nodes.Count > 0)
            .Where(thread => string.Equals(
                thread.Comments.Nodes[0].Author?.Login,
                authoredLogin,
                StringComparison.OrdinalIgnoreCase))
            .Select(thread => new PrThreadStatusEntry(
                thread.Comments.Nodes[0].DatabaseId ?? 0,
                thread.IsResolved ? "Fixed" : "Active",
                thread.Path,
                BuildCommentHistory(thread.Comments.Nodes),
                thread.Comments.Nodes.Count(comment => !string.Equals(
                    comment.Author?.Login,
                    authoredLogin,
                    StringComparison.OrdinalIgnoreCase)),
                // An outdated review thread is one whose diff hunk is no longer part of the current diff,
                // i.e. the anchored code changed after the finding was raised.
                thread.IsOutdated ? ThreadAnchorCodeChange.Changed : ThreadAnchorCodeChange.Unchanged))
            .ToList()
            .AsReadOnly();
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
        CancellationToken ct)
    {
        var parts = repositoryPath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("GitHub repository lookup returned an invalid repository path.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubConnectionVerifier.BuildGraphQlUri(host))
        {
            Content = JsonContent.Create(
                new
                {
                    query = GitHubReviewThreadsQuery,
                    variables = new
                    {
                        owner = parts[0],
                        name = parts[1],
                        pullRequestNumber = pullRequestId,
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

        return payload.Data?.Repository?.PullRequest?.ReviewThreads?.Nodes
               ?? [];
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
            builder.Append(comment.Body ?? string.Empty);
        }

        return builder.ToString();
    }

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record GitHubGraphQlResponse([property: JsonPropertyName("data")] GitHubGraphQlData? Data);

    private sealed record GitHubGraphQlData(
        [property: JsonPropertyName("repository")]
        GitHubGraphQlRepository? Repository);

    private sealed record GitHubGraphQlRepository(
        [property: JsonPropertyName("pullRequest")]
        GitHubGraphQlPullRequest? PullRequest);

    private sealed record GitHubGraphQlPullRequest(
        [property: JsonPropertyName("reviewThreads")]
        GitHubReviewThreadsConnection? ReviewThreads);

    private sealed record GitHubReviewThreadsConnection([property: JsonPropertyName("nodes")] IReadOnlyList<GitHubReviewThreadNode>? Nodes);

    private sealed record GitHubReviewThreadNode(
        [property: JsonPropertyName("isResolved")]
        bool IsResolved,
        [property: JsonPropertyName("isOutdated")]
        bool IsOutdated,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("line")] int? Line,
        [property: JsonPropertyName("comments")]
        GitHubReviewCommentsConnection Comments);

    private sealed record GitHubReviewCommentsConnection([property: JsonPropertyName("nodes")] IReadOnlyList<GitHubReviewCommentNode> Nodes);

    private sealed record GitHubReviewCommentNode(
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("createdAt")]
        DateTimeOffset CreatedAt,
        [property: JsonPropertyName("author")] GitHubActorNode? Author);

    private sealed record GitHubActorNode([property: JsonPropertyName("login")] string? Login);
}
