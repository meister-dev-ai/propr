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
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoCodeReviewQueryService(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : ICodeReviewQueryService
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<ReviewDiscoveryItemDto?> GetReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review query failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ForgejoPullRequestResponse>(ct)
                      ?? throw new InvalidOperationException("Forgejo review query returned an empty payload.");
        var latestRevision = BuildRevision(payload);
        var requestedReviewer = payload.RequestedReviewers?
            .FirstOrDefault(reviewer => !string.IsNullOrWhiteSpace(reviewer.Login)) is { } reviewer
            ? new ReviewerIdentity(
                review.Repository.Host,
                reviewer.Id.ToString(CultureInfo.InvariantCulture),
                reviewer.Login!,
                string.IsNullOrWhiteSpace(reviewer.FullName) ? reviewer.Login! : reviewer.FullName!,
                LooksLikeBot(reviewer.Login))
            : null;

        return new ReviewDiscoveryItemDto(
            ScmProvider.Forgejo,
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

    internal static ReviewRevision BuildRevision(ForgejoPullRequestResponse payload)
    {
        var headSha = payload.Head?.Sha;
        var baseSha = payload.Base?.Sha;
        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(baseSha))
        {
            throw new InvalidOperationException("Forgejo review payload did not include base and head commit SHAs.");
        }

        return new ReviewRevision(headSha, baseSha, baseSha, headSha, $"{baseSha}...{headSha}");
    }

    internal static CodeReviewState MapState(ForgejoPullRequestResponse payload)
    {
        if (payload.Merged || payload.MergedAt is not null)
        {
            return CodeReviewState.Merged;
        }

        if (payload.Draft)
        {
            return CodeReviewState.Draft;
        }

        return string.Equals(payload.State, "open", StringComparison.OrdinalIgnoreCase)
            ? CodeReviewState.Open
            : CodeReviewState.Closed;
    }

    private static bool LooksLikeBot(string? login)
    {
        return !string.IsNullOrWhiteSpace(login)
               && (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("_bot", StringComparison.OrdinalIgnoreCase));
    }

    internal static string BuildRepositoryPath(RepositoryRef repository)
    {
        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return $"{Uri.EscapeDataString(repository.OwnerOrNamespace)}/{Uri.EscapeDataString(repositoryName)}";
    }

    internal sealed record ForgejoPullRequestResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("html_url")]
        string? HtmlUrl,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("merged")] bool Merged,
        [property: JsonPropertyName("merged_at")]
        DateTimeOffset? MergedAt,
        [property: JsonPropertyName("head")] ForgejoBranchResponse? Head,
        [property: JsonPropertyName("base")] ForgejoBranchResponse? Base,
        [property: JsonPropertyName("requested_reviewers")]
        IReadOnlyList<ForgejoUserResponse>? RequestedReviewers);

    internal sealed record ForgejoBranchResponse(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("sha")] string? Sha);

    internal sealed record ForgejoUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("full_name")]
        string? FullName);
}
