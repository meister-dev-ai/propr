// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoCodeReviewPublicationService(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : ICodeReviewPublicationService
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<ReviewCommentPostingDiagnosticsDto> PublishReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity author,
        CancellationToken ct = default,
        ReviewPublicationContext? publicationContext = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var activity = ActivitySource.StartActivity("ForgejoCodeReviewPublicationService.PublishReview");
        activity?.SetTag("scm.provider", ScmProvider.Forgejo.ToString());
        activity?.SetTag("provider.host", review.Repository.Host.HostBaseUrl);
        activity?.SetTag("review.number", review.Number);
        activity?.SetTag("publication.author.login", author.Login);

        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        await this.DeletePendingReviewsAsync(review, author, context.Connection.Secret, ct);
        var payload = BuildPayload(revision, result, author);
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{ForgejoCodeReviewQueryService.BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/reviews"),
            context.Connection.Secret,
            HttpMethod.Post);
        request.Content = JsonContent.Create(payload);

        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        var responseBody = await ReadFailureDetailAsync(response, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(BuildFailureMessage("Forgejo review publication authentication failed.", responseBody));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                BuildFailureMessage(
                    $"Forgejo review publication failed with status {(int)response.StatusCode}.",
                    responseBody));
        }

        // Best-effort capture of the created provider comment ids. The crawler keys retained comments on
        // each review comment's id (from the per-review comments endpoint), not the review id, so the
        // ids are fetched with a follow-up read. Any failure leaves PostedComments empty without
        // disrupting publishing.
        var postedComments = await this.TryCapturePostedCommentsAsync(
            review,
            context.Connection.Secret,
            response,
            ct);

        return ReviewCommentPostingDiagnosticsDto.Empty(
                result.Comments.Count + result.CarriedForwardCandidatesSkipped,
                result.CarriedForwardCandidatesSkipped) with
            {
                PostedCount = result.Comments.Count,
                PostedComments = postedComments,
            };
    }

    private async Task<IReadOnlyList<PostedReviewCommentRef>> TryCapturePostedCommentsAsync(
        CodeReviewRef review,
        string secret,
        HttpResponseMessage reviewResponse,
        CancellationToken ct)
    {
        try
        {
            var createdReview = await reviewResponse.Content
                .ReadFromJsonAsync<ForgejoCreatedReviewResponse>(ct);
            if (createdReview?.Id is not { } reviewId)
            {
                return [];
            }

            var reviewIdText = reviewId.ToString(CultureInfo.InvariantCulture);
            using var commentsRequest = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
                ForgejoConnectionVerifier.BuildApiUri(
                    review.Repository.Host,
                    $"/repos/{ForgejoCodeReviewQueryService.BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/reviews/{reviewIdText}/comments"),
                secret);
            using var commentsResponse = await httpClientFactory.CreateClient("ForgejoProvider")
                .SendAsync(commentsRequest, ct);
            if (!commentsResponse.IsSuccessStatusCode)
            {
                return [];
            }

            var comments = await commentsResponse.Content
                .ReadFromJsonAsync<IReadOnlyList<ForgejoCreatedReviewCommentResponse>>(ct);
            if (comments is null || comments.Count == 0)
            {
                return [];
            }

            return comments
                .Where(comment => comment.Id is not null)
                .Select(comment => new PostedReviewCommentRef(
                    comment.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    reviewIdText,
                    string.IsNullOrWhiteSpace(comment.Path) ? null : comment.Path,
                    comment.Position ?? comment.OriginalPosition))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private async Task DeletePendingReviewsAsync(
        CodeReviewRef review,
        ReviewerIdentity author,
        string secret,
        CancellationToken ct)
    {
        var repositoryPath = ForgejoCodeReviewQueryService.BuildRepositoryPath(review.Repository);
        using var listRequest = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{repositoryPath}/pulls/{review.Number}/reviews",
                "limit=100"),
            secret);
        using var listResponse = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(listRequest, ct);
        var listResponseBody = await ReadFailureDetailAsync(listResponse, ct);
        if (!listResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                BuildFailureMessage(
                    $"Forgejo review cleanup failed while listing existing reviews with status {(int)listResponse.StatusCode}.",
                    listResponseBody));
        }

        var reviews = await listResponse.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullReviewSummary>>(ct)
                      ?? [];
        foreach (var pendingReview in reviews.Where(candidate =>
                     IsPendingReview(candidate) && IsOwnedByReviewer(candidate, author)))
        {
            using var deleteRequest = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
                ForgejoConnectionVerifier.BuildApiUri(
                    review.Repository.Host,
                    $"/repos/{repositoryPath}/pulls/{review.Number}/reviews/{pendingReview.Id}"),
                secret,
                HttpMethod.Delete);
            using var deleteResponse =
                await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(deleteRequest, ct);
            var deleteResponseBody = await ReadFailureDetailAsync(deleteResponse, ct);
            if (deleteResponse.StatusCode != HttpStatusCode.NoContent)
            {
                throw new InvalidOperationException(
                    BuildFailureMessage(
                        $"Forgejo review cleanup failed while deleting pending review {pendingReview.Id} with status {(int)deleteResponse.StatusCode}.",
                        deleteResponseBody));
            }
        }
    }

    private static bool IsPendingReview(ForgejoPullReviewSummary review)
    {
        return string.Equals(review.State, "PENDING", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnedByReviewer(ForgejoPullReviewSummary review, ReviewerIdentity author)
    {
        return review.User is not null
               && ((review.User.Id.HasValue && string.Equals(
                       review.User.Id.Value.ToString(),
                       author.ExternalUserId,
                       StringComparison.OrdinalIgnoreCase))
                   || (!string.IsNullOrWhiteSpace(review.User.Login) && string.Equals(
                       review.User.Login,
                       author.Login,
                       StringComparison.OrdinalIgnoreCase)));
    }

    private static ForgejoCreatePullReviewRequest BuildPayload(
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity author)
    {
        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"## {author.DisplayName} Review");
        summaryBuilder.AppendLine();
        summaryBuilder.AppendLine(result.Summary);

        var inlineComments = new List<ForgejoCreatePullReviewComment>();
        foreach (var comment in result.Comments)
        {
            if (!string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber.HasValue &&
                comment.LineNumber.Value > 0)
            {
                inlineComments.Add(
                    new ForgejoCreatePullReviewComment(
                        $"{FormatSeverity(comment.Severity)}: {comment.Message}",
                        NormalizePath(comment.FilePath),
                        comment.LineNumber.Value,
                        0));
            }
            else
            {
                summaryBuilder.AppendLine();
                summaryBuilder.AppendLine($"- {FormatSeverity(comment.Severity)}: {comment.Message}");
            }
        }

        return new ForgejoCreatePullReviewRequest(
            summaryBuilder.ToString().Trim(),
            LooksLikeCommitSha(revision.HeadSha) ? revision.HeadSha : null,
            "COMMENT",
            inlineComments.Count == 0 ? null : inlineComments);
    }

    private static bool LooksLikeCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/');
    }

    private static string FormatSeverity(CommentSeverity severity)
    {
        return severity switch
        {
            CommentSeverity.Error => "Error",
            CommentSeverity.Warning => "Warning",
            CommentSeverity.Suggestion => "Suggestion",
            _ => "Info",
        };
    }

    private static string BuildFailureMessage(string message, string? responseBody)
    {
        return string.IsNullOrWhiteSpace(responseBody)
            ? message
            : $"{message} Response: {responseBody}";
    }

    private static async Task<string?> ReadFailureDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var singleLineBody = body.ReplaceLineEndings(" ").Trim();
        return singleLineBody.Length <= 240
            ? singleLineBody
            : singleLineBody[..240] + "...";
    }

    private sealed record ForgejoCreatePullReviewRequest(
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("commit_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? CommitId,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("comments")]
        IReadOnlyList<ForgejoCreatePullReviewComment>? Comments);

    private sealed record ForgejoCreatePullReviewComment(
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("new_position")]
        int NewPosition,
        [property: JsonPropertyName("old_position")]
        int OldPosition);

    private sealed record ForgejoCreatedReviewResponse([property: JsonPropertyName("id")] long? Id);

    private sealed record ForgejoCreatedReviewCommentResponse(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("position")]
        int? Position,
        [property: JsonPropertyName("original_position")]
        int? OriginalPosition);

    private sealed record ForgejoPullReviewSummary(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("user")] ForgejoReviewUserSummary? User);

    private sealed record ForgejoReviewUserSummary(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("login")] string? Login);
}
