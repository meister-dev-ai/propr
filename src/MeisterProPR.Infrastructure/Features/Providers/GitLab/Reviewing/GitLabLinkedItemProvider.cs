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
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

/// <summary>
///     Retrieves the GitLab issues a merge request closes, plus on-demand issue detail / discussion
///     lookups. Fails soft: any error yields an empty result.
/// </summary>
internal sealed partial class GitLabLinkedItemProvider(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitLabLinkedItemProvider> logger) : ILinkedItemProvider
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<LinkedItem>> DiscoverLinkedItemsAsync(
        Guid clientId,
        PullRequest pullRequest,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (context, host) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var project = Uri.EscapeDataString(pullRequest.RepositoryId);

            using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
                GitLabConnectionVerifier.BuildApiUri(
                    host,
                    $"/projects/{project}/merge_requests/{pullRequest.PullRequestId}/closes_issues",
                    "per_page=100"),
                context.Connection.Secret);
            using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var issues = await response.Content.ReadFromJsonAsync<IReadOnlyList<IssueResponse>>(cancellationToken) ?? [];
            return issues
                .Select(MapSummary)
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
            var (context, host) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var issue = await this.GetIssueAsync(context, host, pullRequest.RepositoryId, providerKey, cancellationToken);
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
                fields["Labels"] = string.Join(", ", issue.Labels);
            }

            return new LinkedItemDetails(
                providerKey,
                "Issue",
                issue.Title ?? $"Issue #{providerKey}",
                issue.Description,
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
            var (context, host) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var project = Uri.EscapeDataString(pullRequest.RepositoryId);

            using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
                GitLabConnectionVerifier.BuildApiUri(
                    host,
                    $"/projects/{project}/issues/{Uri.EscapeDataString(providerKey)}/notes",
                    "per_page=100"),
                context.Connection.Secret);
            using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var notes = await response.Content.ReadFromJsonAsync<IReadOnlyList<IssueNote>>(cancellationToken) ?? [];
            return notes
                .Where(n => !n.System)
                .Select(n => new LinkedItemComment(n.Author?.Username ?? "Unknown", n.CreatedAt, n.Body ?? string.Empty))
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
            var (context, host) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var issue = await this.GetIssueAsync(context, host, pullRequest.RepositoryId, relatedTargetKey, cancellationToken);
            return issue is null ? null : MapSummary(issue);
        }
        catch (Exception ex)
        {
            LogItemFailed(logger, relatedTargetKey, ex);
            return null;
        }
    }

    private static LinkedItem MapSummary(IssueResponse issue)
    {
        return new LinkedItem(
            issue.Iid.ToString(CultureInfo.InvariantCulture),
            "Issue",
            issue.Title ?? $"Issue #{issue.Iid}",
            issue.Description,
            issue.WebUrl,
            []);
    }

    private async Task<IssueResponse?> GetIssueAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        string issueKey,
        CancellationToken ct)
    {
        var project = Uri.EscapeDataString(repositoryId);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(host, $"/projects/{project}/issues/{Uri.EscapeDataString(issueKey)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<IssueResponse>(ct)
            : null;
    }

    private async Task<(GitLabConnectionVerifier.GitLabConnectionContext Context, ProviderHostRef Host)> ResolveAsync(
        Guid clientId,
        PullRequest pullRequest,
        CancellationToken ct)
    {
        var host = new ProviderHostRef(ScmProvider.GitLab, pullRequest.OrganizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        return (context, host);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Failed to discover linked issues for MR !{PullRequestId}. Proceeding without linked-item context.")]
    private static partial void LogDiscoveryFailed(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch linked issue {IssueKey}.")]
    private static partial void LogItemFailed(ILogger logger, string issueKey, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch discussion for linked issue {IssueKey}.")]
    private static partial void LogDiscussionFailed(ILogger logger, string issueKey, Exception ex);

    private sealed record IssueResponse(
        [property: JsonPropertyName("iid")] int Iid,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")]
        string? Description,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("web_url")]
        string? WebUrl,
        [property: JsonPropertyName("labels")] IReadOnlyList<string>? Labels);

    private sealed record IssueNote(
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("system")] bool System,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("author")] IssueNoteAuthor? Author);

    private sealed record IssueNoteAuthor(
        [property: JsonPropertyName("username")]
        string? Username);
}
