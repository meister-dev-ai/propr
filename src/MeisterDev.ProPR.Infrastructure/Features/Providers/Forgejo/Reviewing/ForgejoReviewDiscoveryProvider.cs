// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Support;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewDiscoveryProvider(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default,
        ReviewDiscoveryContext? context = null)
    {
        var authentication = context is null
            ? await connectionVerifier.VerifyAsync(clientId, repository.Host, ct).ConfigureAwait(false)
            : await connectionVerifier.VerifyAsync(clientId, repository.Host, context, ct).ConfigureAwait(false);
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                repository.Host,
                $"/repos/{ForgejoCodeReviewQueryService.BuildRepositoryPath(repository)}/pulls",
                "state=open&limit=100"),
            authentication.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Support.ForgejoReadFailures.ThrowIfDeniedOrThrottled(response);
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
        return ForgejoCodeReviewQueryService.ContainsAssignedReviewer(payload, reviewer);
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
        var requestedReviewer = ForgejoCodeReviewQueryService.SelectAssignedReviewer(payload, requestedReviewerFilter);

        ReviewerIdentity? mappedReviewer = null;
        if (requestedReviewer is not null)
        {
            var displayName = string.IsNullOrWhiteSpace(requestedReviewer.FullName)
                ? requestedReviewer.Login!
                : requestedReviewer.FullName!;
            mappedReviewer = new ReviewerIdentity(
                repository.Host,
                requestedReviewer.Id.ToString(CultureInfo.InvariantCulture),
                requestedReviewer.Login!,
                displayName,
                IsBot(requestedReviewer.Login));
        }

        return new ReviewDiscoveryItemDto(
            ScmProvider.Forgejo,
            repository,
            review,
            ForgejoCodeReviewQueryService.MapState(payload),
            ForgejoCodeReviewQueryService.BuildRevision(payload),
            mappedReviewer,
            payload.Title ?? $"Pull Request #{payload.Number}",
            payload.HtmlUrl,
            payload.Head?.Ref,
            payload.Base?.Ref,
            ReviewOverviewPresentation.AuthorName(payload.User?.FullName, payload.User?.Login));
    }

    private static bool IsBot(string? login)
    {
        return !string.IsNullOrWhiteSpace(login)
               && (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("_bot", StringComparison.OrdinalIgnoreCase));
    }
}
