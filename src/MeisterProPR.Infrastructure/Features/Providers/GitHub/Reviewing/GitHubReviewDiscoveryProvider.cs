// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewDiscoveryProvider(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, repository.Host, ct);
        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(
                repository.Host,
                $"/repos/{BuildRepositoryPath(repository)}/pulls",
                "state=open&per_page=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub review discovery failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubPullRequestResponse>>(ct)
                      ?? [];

        return payload
            .Where(item => reviewer is null || ContainsRequestedReviewer(item, reviewer))
            .Select(item => ToDiscoveryItem(repository, item))
            .ToList()
            .AsReadOnly();
    }

    private static bool ContainsRequestedReviewer(GitHubPullRequestResponse payload, ReviewerIdentity reviewer)
    {
        return payload.RequestedReviewers?.Any(candidate =>
            (!string.IsNullOrWhiteSpace(candidate.Login) && string.Equals(
                candidate.Login,
                reviewer.Login,
                StringComparison.OrdinalIgnoreCase)) ||
            candidate.Id.ToString(CultureInfo.InvariantCulture) == reviewer.ExternalUserId) == true;
    }

    private static ReviewDiscoveryItemDto ToDiscoveryItem(RepositoryRef repository, GitHubPullRequestResponse payload)
    {
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            payload.Id.ToString(CultureInfo.InvariantCulture),
            payload.Number);
        var latestRevision = BuildRevision(payload);
        var requestedReviewer = payload.RequestedReviewers?
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Login)) is { } reviewer
            ? new ReviewerIdentity(
                repository.Host,
                reviewer.Id.ToString(CultureInfo.InvariantCulture),
                reviewer.Login!,
                string.IsNullOrWhiteSpace(reviewer.Name) ? reviewer.Login! : reviewer.Name!,
                IsBot(reviewer))
            : null;

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitHub,
            repository,
            review,
            MapState(payload),
            latestRevision,
            requestedReviewer,
            payload.Title ?? $"Pull Request #{payload.Number}",
            payload.HtmlUrl,
            payload.Head?.Ref,
            payload.Base?.Ref);
    }

    private static string BuildRepositoryPath(RepositoryRef repository)
    {
        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return $"{Uri.EscapeDataString(repository.OwnerOrNamespace)}/{Uri.EscapeDataString(repositoryName)}";
    }

    private static ReviewRevision BuildRevision(GitHubPullRequestResponse payload)
    {
        var headSha = payload.Head?.Sha;
        var baseSha = payload.Base?.Sha;
        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(baseSha))
        {
            throw new InvalidOperationException("GitHub review discovery payload did not include base and head commit SHAs.");
        }

        return new ReviewRevision(headSha, baseSha, null, headSha, $"{baseSha}...{headSha}");
    }

    private static CodeReviewState MapState(GitHubPullRequestResponse payload)
    {
        if (payload.MergedAt is not null)
        {
            return CodeReviewState.Merged;
        }

        return string.Equals(payload.State, "open", StringComparison.OrdinalIgnoreCase)
            ? CodeReviewState.Open
            : CodeReviewState.Closed;
    }

    private static bool IsBot(GitHubReviewerResponse reviewer)
    {
        return string.Equals(reviewer.Type, "Bot", StringComparison.OrdinalIgnoreCase) ||
               (reviewer.Login?.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private sealed record GitHubPullRequestResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("html_url")]
        string? HtmlUrl,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("merged_at")]
        DateTimeOffset? MergedAt,
        [property: JsonPropertyName("head")] GitHubRefResponse? Head,
        [property: JsonPropertyName("base")] GitHubRefResponse? Base,
        [property: JsonPropertyName("requested_reviewers")]
        IReadOnlyList<GitHubReviewerResponse>? RequestedReviewers);

    private sealed record GitHubRefResponse(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record GitHubReviewerResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type);
}
