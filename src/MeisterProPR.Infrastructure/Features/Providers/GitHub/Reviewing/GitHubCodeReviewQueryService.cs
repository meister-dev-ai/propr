// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubCodeReviewQueryService(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : ICodeReviewQueryService
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<ReviewDiscoveryItemDto?> GetReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub review query failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubPullRequestResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub review query returned an empty payload.");
        var latestRevision = BuildRevision(payload);
        var requestedReviewer = payload.RequestedReviewers?
            .FirstOrDefault(reviewer => !string.IsNullOrWhiteSpace(reviewer.Login)) is { } reviewer
            ? new ReviewerIdentity(
                review.Repository.Host,
                reviewer.Id.ToString(CultureInfo.InvariantCulture),
                reviewer.Login!,
                string.IsNullOrWhiteSpace(reviewer.Name) ? reviewer.Login! : reviewer.Name!,
                IsBot(reviewer))
            : null;

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitHub,
            review.Repository,
            review,
            MapState(payload),
            latestRevision,
            requestedReviewer,
            payload.Title ?? $"Pull Request #{review.Number}",
            payload.HtmlUrl,
            payload.Head?.Ref,
            payload.Base?.Ref);
    }

    public async Task<ReviewRevision?> GetLatestRevisionAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var item = await this.GetReviewAsync(clientId, review, ct);
        return item?.ReviewRevision;
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
            throw new InvalidOperationException("GitHub review payload did not include base and head commit SHAs.");
        }

        return new ReviewRevision(
            headSha,
            baseSha,
            null,
            headSha,
            $"{baseSha}...{headSha}");
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
        return string.Equals(reviewer.Type, "Bot", StringComparison.OrdinalIgnoreCase)
               || (reviewer.Login?.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private sealed record GitHubPullRequestResponse(
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
