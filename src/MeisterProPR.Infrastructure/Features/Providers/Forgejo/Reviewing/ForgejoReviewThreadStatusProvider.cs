// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewThreadStatusProvider(
    ForgejoConnectionVerifier connectionVerifier,
    IClientRegistry clientRegistry,
    IHttpClientFactory httpClientFactory) : IProviderReviewerThreadStatusFetcher
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid clientId,
        CancellationToken ct = default)
    {
        var host = new ProviderHostRef(ScmProvider.Forgejo, organizationUrl);
        var reviewer = await clientRegistry.GetReviewerIdentityAsync(clientId, host, ct);
        if (reviewer is null)
        {
            return [];
        }

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repositoryId, ct);
        var reviews = await this.GetReviewsAsync(context, host, repositoryPath, pullRequestId, ct);

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
            .Where(group => group.Count > 0 && AuthorMatches(group[0].Comment.User, reviewer))
            .Select(group => new PrThreadStatusEntry(
                group[0].Comment.Id,
                DetermineStatus(group, reviewer),
                group[0].Comment.Path,
                BuildCommentHistory(group),
                group.Count(comment => !AuthorMatches(comment.Comment.User, reviewer))))
            .ToList()
            .AsReadOnly();
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
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{pullRequestId}/reviews",
                "limit=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review thread lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullReviewResponse>>(ct)
               ?? [];
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

    private static string BuildThreadKey(ForgejoPullReviewCommentResponse comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.Path))
        {
            var lineNumber = comment.Position ?? comment.OriginalPosition ?? 0;
            return $"{comment.Path}:{lineNumber}";
        }

        return $"comment:{comment.Id}";
    }

    private static bool AuthorMatches(ForgejoUserResponse? user, ReviewerIdentity reviewer)
    {
        return user is not null
               && ((!string.IsNullOrWhiteSpace(user.Login) && string.Equals(
                       user.Login,
                       reviewer.Login,
                       StringComparison.OrdinalIgnoreCase))
                   || user.Id.ToString() == reviewer.ExternalUserId);
    }

    private static string DetermineStatus(
        IReadOnlyList<ForgejoReviewCommentEnvelope> comments,
        ReviewerIdentity reviewer)
    {
        return comments.Any(comment => !AuthorMatches(comment.Comment.User, reviewer) && string.Equals(
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
        [property: JsonPropertyName("user")] ForgejoUserResponse? User);

    private sealed record ForgejoUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login);

    private sealed record ForgejoReviewCommentEnvelope(string? ReviewState, ForgejoPullReviewCommentResponse Comment);
}
