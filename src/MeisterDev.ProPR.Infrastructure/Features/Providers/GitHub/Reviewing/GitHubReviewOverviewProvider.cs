// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewOverviewProvider(GitHubConnectionVerifier connectionVerifier, IHttpClientFactory httpClientFactory) : IReviewOverviewProvider
{
    private const string ThreadsQuery =
        "query OverviewThreads($owner: String!, $name: String!, $number: Int!, $after: String) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { reviewThreads(first: 100, after: $after) { nodes { id isResolved } pageInfo { hasNextPage endCursor } } } } }";

    public ScmProvider Provider => ScmProvider.GitHub;

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
        var session = new ReviewOverviewReadSession();
        var repository = review.Repository;
        var path = BuildRepositoryPath(repository);
        var client = httpClientFactory.CreateClient("GitHubProvider");

        async Task<HttpResponseMessage> GetPage(string endpoint, int page, CancellationToken pageCt)
        {
            using var request = await authentication.CreateAuthenticatedRequestAsync(
                ReviewOverviewReadSession.ScopedUri(
                    GitHubConnectionVerifier.BuildApiUri(repository.Host, endpoint, $"per_page=100&page={page}"),
                    context.ProviderScopePath, repository.Host.HostBaseUrl), ct: pageCt).ConfigureAwait(false);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pageCt).ConfigureAwait(false);
        }

        var conversation = await session.ReadPagesAsync(
            response => new(GitHubPaginationHeaders.ReadGitHubHasMore(response)),
            (page, pageCt) => GetPage($"/repos/{path}/issues/{review.Number}/comments", page, pageCt), ct,
            response => GitHubReadFailures.ThrowIfDeniedOrThrottled(response)).ConfigureAwait(false);
        var reviewComments = await session.ReadPagesAsync(
            response => new(GitHubPaginationHeaders.ReadGitHubHasMore(response)),
            (page, pageCt) => GetPage($"/repos/{path}/pulls/{review.Number}/comments", page, pageCt), ct,
            response => GitHubReadFailures.ThrowIfDeniedOrThrottled(response)).ConfigureAwait(false);
        var reviews = await session.ReadPagesAsync(
            response => new(GitHubPaginationHeaders.ReadGitHubHasMore(response)),
            (page, pageCt) => GetPage($"/repos/{path}/pulls/{review.Number}/reviews", page, pageCt), ct,
            response => GitHubReadFailures.ThrowIfDeniedOrThrottled(response)).ConfigureAwait(false);
        var bodies = reviews.Items.Select(item => ReadReviewBody(item)).ToList();
        var threads = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var complete = false;
        for (var page = 0; page < ProviderCursorPager.MaxPages && session.TryConsume(ct); page++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                ReviewOverviewReadSession.ScopedUri(
                    GitHubConnectionVerifier.BuildGraphQlUri(repository.Host),
                    context.ProviderScopePath, repository.Host.HostBaseUrl))
            {
                Content = JsonContent.Create(
                    new
                    {
                        query = ThreadsQuery,
                        variables = new
                        {
                            owner = repository.OwnerOrNamespace,
                            name = repository.ProjectPath.Split('/').Last(), number = review.Number, after = cursor
                        },
                    }),
            };
            await authentication.AuthorizeRequestAsync(request, ct).ConfigureAwait(false);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var payload = await ReviewOverviewReadSession.ReadJsonAsync(response, ct, response => GitHubReadFailures.ThrowIfDeniedOrThrottled(response))
                .ConfigureAwait(false);
            if (payload is not { } root || HasGraphQlErrors(root))
            {
                break;
            }

            var connection = Nested(root, "data", "repository", "pullRequest", "reviewThreads");
            if (connection is not { } value || ReviewOverviewReadSession.Property(value, "nodes") is not { ValueKind: JsonValueKind.Array } nodes ||
                ReviewOverviewReadSession.Property(value, "pageInfo") is not { } pageInfo || nodes.GetArrayLength() > 100)
            {
                break;
            }

            var valid = true;
            foreach (var thread in nodes.EnumerateArray())
            {
                if (ReviewOverviewReadSession.Identifier(thread) is not { } id || !seen.Add(id))
                {
                    valid = false;
                    break;
                }

                threads.Add(thread.Clone());
            }

            if (!valid)
            {
                break;
            }

            var hasNext = ReviewOverviewReadSession.Boolean(pageInfo, "hasNextPage");
            if (hasNext == false)
            {
                complete = true;
                break;
            }

            if (hasNext != true || ReviewOverviewReadSession.Property(pageInfo, "endCursor") is not { ValueKind: JsonValueKind.String } next)
            {
                break;
            }

            var nextCursor = next.GetString();
            if (string.IsNullOrWhiteSpace(nextCursor) || nextCursor == cursor)
            {
                break;
            }

            cursor = nextCursor;
        }

        var states = threads.Select(thread => ReviewOverviewReadSession.Boolean(thread, "isResolved")).ToList();
        complete &= states.All(state => state.HasValue);
        var total = conversation.Complete && reviewComments.Complete && reviews.Complete && bodies.All(body => body is not null)
            ? conversation.Items.Count + reviewComments.Items.Count + bodies.Count(body => body!.HasBody)
            : (int?)null;
        return new(
            total, complete ? states.Count(state => state == true) : null,
            complete ? states.Count(state => state == false) : null, total.HasValue && complete, true);
    }

    private static bool HasGraphQlErrors(JsonElement root)
    {
        if (ReviewOverviewReadSession.Property(root, "errors") is not { } errors)
        {
            return false;
        }

        if (errors.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var denied = false;
        var throttled = false;
        foreach (var error in errors.EnumerateArray())
        {
            if (ReviewOverviewReadSession.Property(error, "type") is not { ValueKind: JsonValueKind.String } type)
            {
                continue;
            }

            denied |= type.GetString() == "FORBIDDEN";
            throttled |= type.GetString() == "RATE_LIMITED";
        }

        // An explicit permission failure invalidates cached metadata even when a throttle is also reported.
        if (denied)
        {
            throw ProviderReadFailures.Denial(HttpStatusCode.Forbidden);
        }

        if (throttled)
        {
            throw new ProviderThrottledException();
        }

        return errors.GetArrayLength() > 0;
    }

    private static JsonElement? Nested(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (ReviewOverviewReadSession.Property(value, name) is not { } child)
            {
                return null;
            }

            value = child;
        }

        return value;
    }

    private static string BuildRepositoryPath(RepositoryRef repository) =>
        $"{Uri.EscapeDataString(repository.OwnerOrNamespace)}/{Uri.EscapeDataString(repository.ProjectPath.Split('/').Last())}";

    internal sealed record ReviewBody(bool Published, bool HasBody);

    public static ReviewBody? ReadReviewBody(JsonElement review)
    {
        if (ReviewOverviewReadSession.Property(review, "state") is not { ValueKind: JsonValueKind.String } stateValue)
        {
            return null;
        }

        var state = stateValue.GetString();
        if (state == "PENDING")
        {
            return new(false, false);
        }

        var published = state is "APPROVED" or "CHANGES_REQUESTED" or "COMMENTED" or "DISMISSED";
        if (!published || ReviewOverviewReadSession.Property(review, "body") is not { ValueKind: JsonValueKind.String } body ||
            ReviewOverviewReadSession.Property(review, "submitted_at") is not { ValueKind: JsonValueKind.String } timestamp ||
            !timestamp.TryGetDateTimeOffset(out var submitted) || submitted == DateTimeOffset.MinValue)
        {
            return null;
        }

        return new(true, !string.IsNullOrWhiteSpace(body.GetString()));
    }
}
