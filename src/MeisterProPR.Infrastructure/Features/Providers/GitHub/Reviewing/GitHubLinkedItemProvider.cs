// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     Retrieves the GitHub issues linked to a pull request through its closing-issue references, and
///     the on-demand issue detail / discussion lookups. Fails soft: any error yields an empty result.
/// </summary>
internal sealed partial class GitHubLinkedItemProvider(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubLinkedItemProvider> logger) : ILinkedItemProvider
{
    private const string ClosingIssuesQuery =
        "query LinkedIssues($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { closingIssuesReferences(first: 20) { nodes { number title body url } } } } }";

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<IReadOnlyList<LinkedItem>> DiscoverLinkedItemsAsync(
        Guid clientId,
        PullRequest pullRequest,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (context, host, owner, name) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, GitHubConnectionVerifier.BuildGraphQlUri(host))
            {
                Content = JsonContent.Create(
                    new
                    {
                        query = ClosingIssuesQuery,
                        variables = new { owner, name, number = pullRequest.PullRequestId },
                    }),
            };
            await context.AuthorizeRequestAsync(request, cancellationToken);

            using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<GraphQlResponse>(cancellationToken);
            var nodes = payload?.Data?.Repository?.PullRequest?.ClosingIssuesReferences?.Nodes ?? [];

            return nodes
                .Select(n => new LinkedItem(
                    n.Number.ToString(CultureInfo.InvariantCulture),
                    "Issue",
                    n.Title ?? $"Issue #{n.Number}",
                    n.Body,
                    n.Url,
                    []))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            LogDiscoveryFailed(logger, pullRequest.PullRequestId, ex);
            return [];
        }
    }

    public async Task<LinkedItemDetails?> GetItemDetailsAsync(
        Guid clientId,
        PullRequest pullRequest,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (context, host, owner, name) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var issue = await this.GetIssueAsync(context, host, owner, name, providerKey, cancellationToken);
            if (issue is null)
            {
                return null;
            }

            var fields = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(issue.State))
            {
                fields["State"] = issue.State;
            }

            if (issue.Labels is { Count: > 0 })
            {
                fields["Labels"] = string.Join(", ", issue.Labels.Select(l => l.Name).Where(n => !string.IsNullOrEmpty(n)));
            }

            return new LinkedItemDetails(
                providerKey,
                "Issue",
                issue.Title ?? $"Issue #{providerKey}",
                issue.Body,
                issue.State,
                fields,
                []);
        }
        catch (Exception ex)
        {
            LogItemFailed(logger, providerKey, ex);
            return null;
        }
    }

    public async Task<IReadOnlyList<LinkedItemComment>> GetItemDiscussionAsync(
        Guid clientId,
        PullRequest pullRequest,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (context, host, owner, name) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            using var request = await context.CreateAuthenticatedRequestAsync(
                GitHubConnectionVerifier.BuildApiUri(
                    host,
                    $"/repos/{owner}/{name}/issues/{Uri.EscapeDataString(providerKey)}/comments",
                    "per_page=100"),
                ct: cancellationToken);
            using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var comments = await response.Content.ReadFromJsonAsync<IReadOnlyList<IssueComment>>(cancellationToken) ?? [];
            return comments
                .Select(c => new LinkedItemComment(c.User?.Login ?? "Unknown", c.CreatedAt, c.Body ?? string.Empty))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            LogDiscussionFailed(logger, providerKey, ex);
            return [];
        }
    }

    public async Task<LinkedItem?> ResolveRelatedLinkAsync(
        Guid clientId,
        PullRequest pullRequest,
        string relatedTargetKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (context, host, owner, name) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var issue = await this.GetIssueAsync(context, host, owner, name, relatedTargetKey, cancellationToken);
            return issue is null
                ? null
                : new LinkedItem(relatedTargetKey, "Issue", issue.Title ?? $"Issue #{relatedTargetKey}", issue.Body, issue.HtmlUrl, []);
        }
        catch (Exception ex)
        {
            LogItemFailed(logger, relatedTargetKey, ex);
            return null;
        }
    }

    private async Task<IssueResponse?> GetIssueAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string owner,
        string name,
        string issueKey,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{owner}/{name}/issues/{Uri.EscapeDataString(issueKey)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<IssueResponse>(ct)
            : null;
    }

    private async Task<(GitHubConnectionVerifier.GitHubConnectionContext Context, ProviderHostRef Host, string Owner, string Name)>
        ResolveAsync(Guid clientId, PullRequest pullRequest, CancellationToken ct)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, pullRequest.OrganizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var (owner, name) = await this.ResolveOwnerNameAsync(context, host, pullRequest.RepositoryId, ct);
        return (context, host, owner, name);
    }

    private async Task<(string Owner, string Name)> ResolveOwnerNameAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(repositoryId) && repositoryId.Contains('/', StringComparison.Ordinal))
        {
            var parts = repositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                return (parts[0], parts[1]);
            }
        }

        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository lookup failed with status {(int)response.StatusCode}.");
        }

        var repository = await response.Content.ReadFromJsonAsync<RepositoryResponse>(ct);
        var fullName = repository?.FullName?.Trim();
        if (string.IsNullOrWhiteSpace(fullName) || !fullName.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GitHub repository lookup did not return an owner/name path.");
        }

        var resolved = fullName.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (resolved[0], resolved[1]);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Failed to discover linked issues for PR #{PullRequestId}. Proceeding without linked-item context.")]
    private static partial void LogDiscoveryFailed(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch linked issue {IssueKey}.")]
    private static partial void LogItemFailed(ILogger logger, string issueKey, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch discussion for linked issue {IssueKey}.")]
    private static partial void LogDiscussionFailed(ILogger logger, string issueKey, Exception ex);

    private sealed record GraphQlResponse([property: JsonPropertyName("data")] GraphQlData? Data);

    private sealed record GraphQlData(
        [property: JsonPropertyName("repository")]
        GraphQlRepository? Repository);

    private sealed record GraphQlRepository(
        [property: JsonPropertyName("pullRequest")]
        GraphQlPullRequest? PullRequest);

    private sealed record GraphQlPullRequest(
        [property: JsonPropertyName("closingIssuesReferences")]
        ClosingIssuesConnection? ClosingIssuesReferences);

    private sealed record ClosingIssuesConnection([property: JsonPropertyName("nodes")] IReadOnlyList<ClosingIssueNode>? Nodes);

    private sealed record ClosingIssueNode(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record RepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record IssueResponse(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("html_url")]
        string? HtmlUrl,
        [property: JsonPropertyName("labels")] IReadOnlyList<IssueLabel>? Labels);

    private sealed record IssueLabel([property: JsonPropertyName("name")] string? Name);

    private sealed record IssueComment(
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("user")] IssueCommentUser? User);

    private sealed record IssueCommentUser([property: JsonPropertyName("login")] string? Login);
}
