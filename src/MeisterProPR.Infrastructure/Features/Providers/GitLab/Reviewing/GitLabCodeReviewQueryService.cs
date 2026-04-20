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
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabCodeReviewQueryService(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : ICodeReviewQueryService
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<ReviewDiscoveryItemDto?> GetReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/projects/{Uri.EscapeDataString(review.Repository.ExternalRepositoryId)}/merge_requests/{review.Number}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab review query failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitLabMergeRequestResponse>(ct)
                      ?? throw new InvalidOperationException("GitLab review query returned an empty payload.");
        var latestRevision = BuildRevision(payload);
        var requestedReviewer = payload.Reviewers?
            .FirstOrDefault(reviewer => !string.IsNullOrWhiteSpace(reviewer.Username)) is { } reviewer
            ? new ReviewerIdentity(
                review.Repository.Host,
                reviewer.Id.ToString(CultureInfo.InvariantCulture),
                reviewer.Username!,
                string.IsNullOrWhiteSpace(reviewer.Name) ? reviewer.Username! : reviewer.Name!,
                reviewer.Bot)
            : null;

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitLab,
            review.Repository,
            review,
            MapState(payload.State),
            latestRevision,
            requestedReviewer,
            payload.Title ?? $"Merge Request !{review.Number}",
            payload.WebUrl,
            payload.SourceBranch,
            payload.TargetBranch);
    }

    public async Task<ReviewRevision?> GetLatestRevisionAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var item = await this.GetReviewAsync(clientId, review, ct);
        return item?.ReviewRevision;
    }

    internal static ReviewRevision BuildRevision(GitLabMergeRequestResponse payload)
    {
        var headSha = NormalizeOptional(payload.DiffRefs?.HeadSha) ?? NormalizeOptional(payload.Sha);
        if (string.IsNullOrWhiteSpace(headSha))
        {
            throw new InvalidOperationException("GitLab review payload did not include a head commit SHA.");
        }

        var baseSha = NormalizeOptional(payload.DiffRefs?.BaseSha)
                      ?? NormalizeOptional(payload.DiffRefs?.StartSha)
                      ?? headSha;
        var startSha = NormalizeOptional(payload.DiffRefs?.StartSha) ?? baseSha;

        return new ReviewRevision(headSha, baseSha, startSha, headSha, $"{baseSha}...{headSha}");
    }

    internal static CodeReviewState MapState(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "opened" => CodeReviewState.Open,
            "merged" => CodeReviewState.Merged,
            _ => CodeReviewState.Closed,
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal sealed record GitLabMergeRequestResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("iid")] int Iid,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("web_url")]
        string? WebUrl,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("source_branch")]
        string? SourceBranch,
        [property: JsonPropertyName("target_branch")]
        string? TargetBranch,
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("diff_refs")]
        GitLabDiffRefsResponse? DiffRefs,
        [property: JsonPropertyName("reviewers")]
        IReadOnlyList<GitLabReviewerResponse>? Reviewers);

    internal sealed record GitLabDiffRefsResponse(
        [property: JsonPropertyName("base_sha")]
        string? BaseSha,
        [property: JsonPropertyName("head_sha")]
        string? HeadSha,
        [property: JsonPropertyName("start_sha")]
        string? StartSha);

    internal sealed record GitLabReviewerResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("username")]
        string? Username,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("bot")] bool Bot,
        [property: JsonPropertyName("re_requested")]
        bool ReRequested);
}
