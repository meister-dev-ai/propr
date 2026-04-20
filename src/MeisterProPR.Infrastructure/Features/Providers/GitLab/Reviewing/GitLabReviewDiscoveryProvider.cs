// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewDiscoveryProvider(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, repository.Host, ct);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                repository.Host,
                $"/projects/{Uri.EscapeDataString(repository.ExternalRepositoryId)}/merge_requests",
                "state=opened&per_page=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab review discovery failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content
                          .ReadFromJsonAsync<IReadOnlyList<GitLabCodeReviewQueryService.GitLabMergeRequestResponse>>(ct)
                      ?? [];

        return payload
            .Where(item => reviewer is null || ContainsRequestedReviewer(item, reviewer))
            .Select(item => ToDiscoveryItem(repository, item, reviewer))
            .ToList()
            .AsReadOnly();
    }

    private static bool ContainsRequestedReviewer(
        GitLabCodeReviewQueryService.GitLabMergeRequestResponse payload,
        ReviewerIdentity reviewer)
    {
        return payload.Reviewers?.Any(candidate =>
            (!string.IsNullOrWhiteSpace(candidate.Username) && string.Equals(
                candidate.Username,
                reviewer.Login,
                StringComparison.OrdinalIgnoreCase)) ||
            candidate.Id.ToString(CultureInfo.InvariantCulture) == reviewer.ExternalUserId) == true;
    }

    private static ReviewDiscoveryItemDto ToDiscoveryItem(
        RepositoryRef repository,
        GitLabCodeReviewQueryService.GitLabMergeRequestResponse payload,
        ReviewerIdentity? requestedReviewerFilter)
    {
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            payload.Id.ToString(CultureInfo.InvariantCulture),
            payload.Iid);
        var requestedReviewer = payload.Reviewers?
                                    .FirstOrDefault(candidate =>
                                        !string.IsNullOrWhiteSpace(candidate.Username)
                                        && (requestedReviewerFilter is null
                                            || string.Equals(
                                                candidate.Username,
                                                requestedReviewerFilter.Login,
                                                StringComparison.OrdinalIgnoreCase)
                                            || candidate.Id.ToString(CultureInfo.InvariantCulture) ==
                                            requestedReviewerFilter.ExternalUserId))
                                ?? payload.Reviewers?.FirstOrDefault(candidate =>
                                    !string.IsNullOrWhiteSpace(candidate.Username));

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitLab,
            repository,
            review,
            GitLabCodeReviewQueryService.MapState(payload.State),
            GitLabCodeReviewQueryService.BuildRevision(payload),
            requestedReviewer is null
                ? null
                : new ReviewerIdentity(
                    repository.Host,
                    requestedReviewer.Id.ToString(CultureInfo.InvariantCulture),
                    requestedReviewer.Username!,
                    string.IsNullOrWhiteSpace(requestedReviewer.Name)
                        ? requestedReviewer.Username!
                        : requestedReviewer.Name!,
                    requestedReviewer.Bot),
            payload.Title ?? $"Merge Request !{payload.Iid}",
            payload.WebUrl,
            payload.SourceBranch,
            payload.TargetBranch);
    }
}
