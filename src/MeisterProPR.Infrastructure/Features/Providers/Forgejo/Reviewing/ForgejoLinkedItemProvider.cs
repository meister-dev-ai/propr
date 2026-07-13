// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

/// <summary>
///     Retrieves the Forgejo issues a pull request references. Forgejo has no first-class PR-to-issue
///     link API, so linked issues are parsed as <c>#N</c> references from the pull-request title and
///     body and resolved through the issue API. Fails soft: any error yields an empty result.
/// </summary>
internal sealed partial class ForgejoLinkedItemProvider(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<ForgejoLinkedItemProvider> logger) : ILinkedItemProvider
{
    // Cap on distinct references resolved per PR so a body citing many "#N" tokens cannot fan out into an
    // unbounded run of sequential issue lookups (rate-limit / latency guard). The eager count cap trims further.
    private const int MaxReferencesToResolve = 20;

    public ScmProvider Provider => ScmProvider.Forgejo;

    // Negative lookbehind on a word char so "C#9", a hex colour "#abc123" won't fire falsely, and a cross-repo
    // "owner/other#5" reference is not mistaken for a local issue. Residual pure-digit false positives 404 and drop.
    [GeneratedRegex(@"(?<!\w)#(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex IssueReferenceRegex();

    public async Task<IReadOnlyList<LinkedItem>> DiscoverLinkedItemsAsync(
        Guid clientId,
        PullRequest pullRequest,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var references = ExtractIssueReferences(pullRequest.Title, pullRequest.Description);
            if (references.Count == 0)
            {
                return [];
            }

            var (context, host, repositoryPath) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);

            var items = new List<LinkedItem>();
            foreach (var reference in references.Take(MaxReferencesToResolve))
            {
                var issue = await this.GetIssueAsync(context, host, repositoryPath, reference, cancellationToken);
                if (issue is not null)
                {
                    items.Add(MapSummary(issue, reference));
                }
            }

            return items.AsReadOnly();
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
            var (context, host, repositoryPath) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var issue = await this.GetIssueAsync(context, host, repositoryPath, providerKey, cancellationToken);
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
            var (context, host, repositoryPath) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
                ForgejoConnectionVerifier.BuildApiUri(
                    host,
                    $"/repos/{repositoryPath}/issues/{Uri.EscapeDataString(providerKey)}/comments",
                    "limit=100"),
                context.Connection.Secret);
            using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, cancellationToken);
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
            var (context, host, repositoryPath) = await this.ResolveAsync(clientId, pullRequest, cancellationToken);
            var issue = await this.GetIssueAsync(context, host, repositoryPath, relatedTargetKey, cancellationToken);
            return issue is null ? null : MapSummary(issue, relatedTargetKey);
        }
        catch (Exception ex)
        {
            LogItemFailed(logger, relatedTargetKey, ex);
            return null;
        }
    }

    internal static IReadOnlyList<string> ExtractIssueReferences(string? title, string? body)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var source in new[] { title, body })
        {
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            foreach (Match match in IssueReferenceRegex().Matches(source))
            {
                var key = match.Groups[1].Value;
                if (seen.Add(key))
                {
                    result.Add(key);
                }
            }
        }

        return result;
    }

    private static LinkedItem MapSummary(IssueResponse issue, string fallbackKey)
    {
        var key = issue.Number > 0 ? issue.Number.ToString(CultureInfo.InvariantCulture) : fallbackKey;
        return new LinkedItem(key, "Issue", issue.Title ?? $"Issue #{key}", issue.Body, issue.HtmlUrl, []);
    }

    private async Task<IssueResponse?> GetIssueAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        string issueKey,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/issues/{Uri.EscapeDataString(issueKey)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<IssueResponse>(ct)
            : null;
    }

    private async Task<(ForgejoConnectionVerifier.ForgejoConnectionContext Context, ProviderHostRef Host, string RepositoryPath)>
        ResolveAsync(Guid clientId, PullRequest pullRequest, CancellationToken ct)
    {
        var host = new ProviderHostRef(ScmProvider.Forgejo, pullRequest.OrganizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, pullRequest.RepositoryId, ct);
        return (context, host, repositoryPath);
    }

    private async Task<string> ResolveRepositoryPathAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(repositoryId) && repositoryId.Contains('/', StringComparison.Ordinal))
        {
            return string.Join(
                '/',
                repositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Uri.EscapeDataString));
        }

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo repository lookup failed with status {(int)response.StatusCode}.");
        }

        var repository = await response.Content.ReadFromJsonAsync<RepositoryResponse>(ct);
        var fullName = repository?.FullName?.Trim();
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new InvalidOperationException("Forgejo repository lookup did not return a repository full name.");
        }

        return string.Join(
            '/',
            fullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Failed to discover linked issues for PR #{PullRequestId}. Proceeding without linked-item context.")]
    private static partial void LogDiscoveryFailed(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch linked issue {IssueKey}.")]
    private static partial void LogItemFailed(ILogger logger, string issueKey, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch discussion for linked issue {IssueKey}.")]
    private static partial void LogDiscussionFailed(ILogger logger, string issueKey, Exception ex);

    private sealed record RepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record IssueResponse(
        [property: JsonPropertyName("number")] int Number,
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
