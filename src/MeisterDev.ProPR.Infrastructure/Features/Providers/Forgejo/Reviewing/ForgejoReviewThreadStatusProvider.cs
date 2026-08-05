// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewThreadStatusProvider(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IProviderReviewerThreadStatusFetcher
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        ThreadOwnershipResolver ownership,
        Guid clientId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        var host = new ProviderHostRef(ScmProvider.Forgejo, organizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repositoryId, ct);
        var reviews = await this.GetReviewsAsync(context, host, repositoryPath, pullRequestId, ct);

        // Forgejo names an author by login, and the connection verification above is the only place the
        // authenticated one exists, so the pass's resolver is completed with it here. It is contributed into
        // the instance the caller handed down, so the consumers that run later in the same pass, and never see
        // a connection of their own, decide with the identity too.
        ownership.ContributeIdentity(new ThreadOwnerIdentity(Login: context.AuthenticatedUsername));

        var flattenedComments = new List<ForgejoReviewCommentEnvelope>();
        foreach (var review in reviews)
        {
            var comments = await this.GetReviewCommentsAsync(
                context,
                host,
                repositoryPath,
                pullRequestId,
                review.Id,
                ct);
            flattenedComments.AddRange(comments.Select(comment => new ForgejoReviewCommentEnvelope(review.State, comment)));
        }

        return flattenedComments
            .GroupBy(comment => BuildThreadKey(comment.Comment))
            .Select(group => group.OrderBy(comment => comment.Comment.CreatedAt)
                .ThenBy(comment => comment.Comment.Id)
                .ToList())
            .Where(group => group.Count > 0 && ownership.OwnsThread(ToCommentRef(group[0].Comment)))
            .Select(group => new PrThreadStatusEntry(
                // Forgejo has no thread object: these groups are ProPR's own, keyed on path and position, and
                // nothing the API accepts addresses one. The identifier is absent rather than borrowed from
                // the first comment, which would hand callers a handle that resolves to a comment.
                null,
                DetermineStatus(group, ownership),
                group[0].Comment.Path,
                BuildCommentHistory(group),
                group.Count(comment => !ownership.OwnsComment(ToCommentRef(comment.Comment))),
                DetermineCodeChange(group)))
            .ToList()
            .AsReadOnly();
    }

    // An invalidated review comment is one whose diff line no longer exists in the current diff, i.e.
    // the anchored code changed. When no comment is flagged invalidated the signal is left undetermined
    // rather than asserting the code is unchanged, so an absent flag never grants a false "fixed".
    private static ThreadAnchorCodeChange DetermineCodeChange(IReadOnlyList<ForgejoReviewCommentEnvelope> comments)
    {
        return comments.Any(comment => comment.Comment.Invalidated)
            ? ThreadAnchorCodeChange.Changed
            : ThreadAnchorCodeChange.Unknown;
    }

    private async Task<string> ResolveRepositoryPathAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken ct)
    {
        if (LooksLikeRepositoryPath(repositoryId))
        {
            return NormalizeRepositoryPath(repositoryId);
        }

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Forgejo repository lookup failed because the repository could not be found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo repository lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ForgejoRepositoryResponse>(ct)
                      ?? throw new InvalidOperationException("Forgejo repository lookup returned an empty payload.");
        if (string.IsNullOrWhiteSpace(payload.FullName))
        {
            throw new InvalidOperationException("Forgejo repository lookup did not return a repository full name.");
        }

        return payload.FullName.Trim();
    }

    private static bool LooksLikeRepositoryPath(string repositoryId)
    {
        return !string.IsNullOrWhiteSpace(repositoryId)
               && repositoryId.Contains('/', StringComparison.Ordinal);
    }

    private static string NormalizeRepositoryPath(string repositoryPath)
    {
        return string.Join(
            '/',
            repositoryPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
    }

    private async Task<IReadOnlyList<ForgejoPullReviewResponse>> GetReviewsAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        return await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.GetReviewPageAsync(
                context,
                host,
                repositoryPath,
                pullRequestId,
                page,
                pageSize,
                pageCt),
            review => review.Id.ToString(CultureInfo.InvariantCulture),
            $"Forgejo's review listing for pull request {pullRequestId}",
            ct);
    }

    private async Task<ProviderRestPager.RestPage<ForgejoPullReviewResponse>> GetReviewPageAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{pullRequestId}/reviews",
                BuildPageQuery(page, pageSize)),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review thread lookup failed with status {(int)response.StatusCode}.");
        }

        var reviews = await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullReviewResponse>>(ct)
                      ?? [];

        return new ProviderRestPager.RestPage<ForgejoPullReviewResponse>(
            reviews,
            TotalCount: ProviderPaginationHeaders.ReadForgejoTotalCount(response));
    }

    private async Task<IReadOnlyList<ForgejoPullReviewCommentResponse>> GetReviewCommentsAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        long reviewId,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{pullRequestId}/reviews/{reviewId}/comments"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review comment lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullReviewCommentResponse>>(ct)
               ?? [];
    }

    // Forgejo clamps a requested page size to the host's configured maximum response length, which is why the
    // read follows the total it reports rather than counting requests. The first page asks for a size and no
    // page number, which is the request a single-page collection made before it was read across pages.
    private static string BuildPageQuery(int page, int pageSize)
    {
        var size = $"limit={pageSize.ToString(CultureInfo.InvariantCulture)}";

        return page <= 1 ? size : $"page={page.ToString(CultureInfo.InvariantCulture)}&{size}";
    }

    private static string BuildThreadKey(ForgejoPullReviewCommentResponse comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.Path))
        {
            var lineNumber = comment.Position ?? comment.OriginalPosition ?? 0;
            return $"{comment.Path}:{lineNumber}";
        }

        return $"comment:{comment.Id}";
    }

    // A Forgejo review comment id is unique within the pull request, so provenance resolves on it alone. The
    // thread id recorded at publish time is the review id, which this listing does not carry per comment.
    private static ThreadCommentRef ToCommentRef(ForgejoPullReviewCommentResponse comment)
    {
        return new ThreadCommentRef(
            null,
            comment.Id.ToString(CultureInfo.InvariantCulture),
            AuthorLogin: comment.User?.Login);
    }

    private static string DetermineStatus(
        IReadOnlyList<ForgejoReviewCommentEnvelope> comments,
        ThreadOwnershipResolver ownership)
    {
        return comments.Any(comment => !ownership.OwnsComment(ToCommentRef(comment.Comment)) && string.Equals(
            comment.ReviewState,
            "APPROVED",
            StringComparison.OrdinalIgnoreCase))
            ? "Fixed"
            : "Active";
    }

    private static string BuildCommentHistory(IReadOnlyList<ForgejoReviewCommentEnvelope> comments)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < comments.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(comments[index].Comment.User?.Login ?? "Unknown");
            builder.Append(": ");
            builder.Append(comments[index].Comment.Body ?? string.Empty);
        }

        return builder.ToString();
    }

    private sealed record ForgejoRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record ForgejoPullReviewResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("user")] ForgejoUserResponse? User);

    private sealed record ForgejoPullReviewCommentResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("position")]
        int? Position,
        [property: JsonPropertyName("original_position")]
        int? OriginalPosition,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset CreatedAt,
        [property: JsonPropertyName("user")] ForgejoUserResponse? User,
        [property: JsonPropertyName("invalidated")]
        bool Invalidated = false);

    private sealed record ForgejoUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login);

    private sealed record ForgejoReviewCommentEnvelope(string? ReviewState, ForgejoPullReviewCommentResponse Comment);
}
