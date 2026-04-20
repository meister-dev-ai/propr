// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewDiscoveryProvider(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, repository.Host, ct);
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                repository.Host,
                $"/repos/{ForgejoCodeReviewQueryService.BuildRepositoryPath(repository)}/pulls",
                "state=open&limit=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review discovery failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content
                          .ReadFromJsonAsync<IReadOnlyList<ForgejoCodeReviewQueryService.ForgejoPullRequestResponse>>(ct)
                      ?? [];

        return payload
            .Where(item => reviewer is null || ContainsRequestedReviewer(item, reviewer))
            .Select(item => ToDiscoveryItem(repository, item, reviewer))
            .ToList()
            .AsReadOnly();
    }

    private static bool ContainsRequestedReviewer(
        ForgejoCodeReviewQueryService.ForgejoPullRequestResponse payload,
        ReviewerIdentity reviewer)
    {
        return payload.RequestedReviewers?.Any(candidate =>
            (!string.IsNullOrWhiteSpace(candidate.Login) && string.Equals(
                candidate.Login,
                reviewer.Login,
                StringComparison.OrdinalIgnoreCase))
            || candidate.Id.ToString(CultureInfo.InvariantCulture) == reviewer.ExternalUserId) == true;
    }

    private static ReviewDiscoveryItemDto ToDiscoveryItem(
        RepositoryRef repository,
        ForgejoCodeReviewQueryService.ForgejoPullRequestResponse payload,
        ReviewerIdentity? requestedReviewerFilter)
    {
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            payload.Id.ToString(CultureInfo.InvariantCulture),
            payload.Number);
        var requestedReviewer = payload.RequestedReviewers?
                                    .FirstOrDefault(candidate =>
                                        !string.IsNullOrWhiteSpace(candidate.Login)
                                        && (requestedReviewerFilter is null
                                            || string.Equals(
                                                candidate.Login,
                                                requestedReviewerFilter.Login,
                                                StringComparison.OrdinalIgnoreCase)
                                            || candidate.Id.ToString(CultureInfo.InvariantCulture) ==
                                            requestedReviewerFilter.ExternalUserId))
                                ?? payload.RequestedReviewers?.FirstOrDefault(candidate =>
                                    !string.IsNullOrWhiteSpace(candidate.Login));

        return new ReviewDiscoveryItemDto(
            ScmProvider.Forgejo,
            repository,
            review,
            ForgejoCodeReviewQueryService.MapState(payload),
            ForgejoCodeReviewQueryService.BuildRevision(payload),
            requestedReviewer is null
                ? null
                : new ReviewerIdentity(
                    repository.Host,
                    requestedReviewer.Id.ToString(CultureInfo.InvariantCulture),
                    requestedReviewer.Login!,
                    string.IsNullOrWhiteSpace(requestedReviewer.FullName)
                        ? requestedReviewer.Login!
                        : requestedReviewer.FullName!,
                    IsBot(requestedReviewer.Login)),
            payload.Title ?? $"Pull Request #{payload.Number}",
            payload.HtmlUrl,
            payload.Head?.Ref,
            payload.Base?.Ref);
    }

    private static bool IsBot(string? login)
    {
        return !string.IsNullOrWhiteSpace(login)
               && (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("_bot", StringComparison.OrdinalIgnoreCase));
    }
}
