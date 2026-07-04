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
        ReviewerIdentity? requestedReviewer = null;
        if (SelectAssignedReviewer(payload) is { } reviewer)
        {
            var displayName = string.IsNullOrWhiteSpace(reviewer.FullName) ? reviewer.Login! : reviewer.FullName!;
            requestedReviewer = new ReviewerIdentity(
                review.Repository.Host,
                reviewer.Id.ToString(CultureInfo.InvariantCulture),
                reviewer.Login!,
                displayName,
                LooksLikeBot(reviewer.Login));
        }

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

    internal static bool ContainsAssignedReviewer(
        ForgejoPullRequestResponse payload,
        ReviewerIdentity reviewer)
    {
        return EnumerateAssignedReviewers(payload).Any(candidate => MatchesReviewer(candidate, reviewer));
    }

    internal static ForgejoUserResponse? SelectAssignedReviewer(
        ForgejoPullRequestResponse payload,
        ReviewerIdentity? reviewerFilter = null)
    {
        var candidates = EnumerateAssignedReviewers(payload).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (reviewerFilter is null)
        {
            return candidates[0];
        }

        return candidates.FirstOrDefault(candidate => MatchesReviewer(candidate, reviewerFilter)) ?? candidates[0];
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

    private static IEnumerable<ForgejoUserResponse> EnumerateAssignedReviewers(ForgejoPullRequestResponse payload)
    {
        var requestedReviewers = payload.RequestedReviewers?
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Login))
            .ToArray();
        if (requestedReviewers is { Length: > 0 })
        {
            return requestedReviewers;
        }

        var assignees = payload.Assignees?
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Login))
            .ToArray();
        if (assignees is { Length: > 0 })
        {
            return assignees;
        }

        return payload.Assignee is { } assignee && !string.IsNullOrWhiteSpace(assignee.Login)
            ? [assignee]
            : [];
    }

    private static bool MatchesReviewer(ForgejoUserResponse candidate, ReviewerIdentity reviewer)
    {
        return (!string.IsNullOrWhiteSpace(candidate.Login)
                && string.Equals(candidate.Login, reviewer.Login, StringComparison.OrdinalIgnoreCase))
               || candidate.Id.ToString(CultureInfo.InvariantCulture) == reviewer.ExternalUserId;
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
        [property: JsonPropertyName("assignee")]
        ForgejoUserResponse? Assignee,
        [property: JsonPropertyName("assignees")]
        IReadOnlyList<ForgejoUserResponse>? Assignees,
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
