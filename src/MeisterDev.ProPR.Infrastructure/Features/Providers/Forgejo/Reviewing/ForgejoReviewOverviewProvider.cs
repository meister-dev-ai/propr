// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewOverviewProvider(ForgejoConnectionVerifier connectionVerifier, IHttpClientFactory httpClientFactory)
    : IReviewOverviewProvider
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<ReviewOverviewDto> GetOverviewAsync(Guid clientId, CodeReviewRef review, ReviewDiscoveryContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await this.ReadOverviewAsync(clientId, review, context, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The provider could not return pull request metadata.");
        }
    }

    private async Task<ReviewOverviewDto> ReadOverviewAsync(Guid clientId, CodeReviewRef review, ReviewDiscoveryContext context, CancellationToken ct)
    {
        var authentication = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, context, ct).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient("ForgejoProvider");
        var session = new ReviewOverviewReadSession();
        var path = ForgejoCodeReviewQueryService.BuildRepositoryPath(review.Repository);

        async Task<HttpResponseMessage> GetPage(string endpoint, int page, CancellationToken pageCt)
        {
            using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
                ReviewOverviewReadSession.ScopedUri(
                    ForgejoConnectionVerifier.BuildApiUri(
                        review.Repository.Host,
                        endpoint, $"limit=100&page={page}"), context.ProviderScopePath, review.Repository.Host.HostBaseUrl), authentication.Connection.Secret);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pageCt).ConfigureAwait(false);
        }

        var conversation = await session.ReadPagesAsync(
            response => new(TotalCount: ForgejoPaginationHeaders.ReadForgejoTotalCount(response)),
            (page, pageCt) => GetPage($"/repos/{path}/issues/{review.Number}/comments", page, pageCt), ct,
            response => ForgejoReadFailures.ThrowIfDeniedOrThrottled(response)).ConfigureAwait(false);
        var reviews = await session.ReadPagesAsync(
            response => new(TotalCount: ForgejoPaginationHeaders.ReadForgejoTotalCount(response)),
            (page, pageCt) => GetPage($"/repos/{path}/pulls/{review.Number}/reviews", page, pageCt), ct,
            response => ForgejoReadFailures.ThrowIfDeniedOrThrottled(response)).ConfigureAwait(false);
        var total = conversation.Items.Count;
        var complete = conversation.Complete && reviews.Complete;
        var commentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nativeReview in reviews.Items)
        {
            var body = ReadReviewBody(nativeReview);
            if (body is null)
            {
                complete = false;
                continue;
            }

            if (!body.Published)
            {
                continue;
            }

            if (body.HasBody)
            {
                total++;
            }

            var id = ReviewOverviewReadSession.Identifier(nativeReview)!;
            var comments = await session.ReadPagesAsync(
                    response => new(TotalCount: ForgejoPaginationHeaders.ReadForgejoTotalCount(response)),
                    (page, pageCt) => GetPage($"/repos/{path}/pulls/{review.Number}/reviews/{Uri.EscapeDataString(id)}/comments", page, pageCt), ct,
                    response => ForgejoReadFailures.ThrowIfDeniedOrThrottled(response))
                .ConfigureAwait(false);
            complete &= comments.Complete;
            foreach (var comment in comments.Items)
            {
                if (!commentIds.Add(ReviewOverviewReadSession.Identifier(comment)!))
                {
                    complete = false;
                }
                else
                {
                    total++;
                }
            }

            if (!comments.Complete)
            {
                break;
            }
        }

        return new(complete ? total : null, null, null, complete, false);
    }

    internal sealed record ReviewBody(bool Published, bool HasBody);

    public static ReviewBody? ReadReviewBody(JsonElement review)
    {
        if (ReviewOverviewReadSession.Property(review, "state") is not { ValueKind: JsonValueKind.String } stateValue)
        {
            return null;
        }

        var state = stateValue.GetString();
        if (state is "PENDING" or "REQUEST_REVIEW")
        {
            return new(false, false);
        }

        var published = state is "APPROVED" or "REQUEST_CHANGES" or "COMMENT";
        if (!published || ReviewOverviewReadSession.Property(review, "body") is not { ValueKind: JsonValueKind.String } body ||
            ReviewOverviewReadSession.Property(review, "submitted_at") is not { ValueKind: JsonValueKind.String } timestamp ||
            !timestamp.TryGetDateTimeOffset(out var submitted) || submitted == DateTimeOffset.MinValue)
        {
            return null;
        }

        return new(true, !string.IsNullOrWhiteSpace(body.GetString()));
    }
}
