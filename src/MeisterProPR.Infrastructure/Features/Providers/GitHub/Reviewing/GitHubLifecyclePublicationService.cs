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
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed partial class GitHubLifecyclePublicationService(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubLifecyclePublicationService>? logger = null)
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");
    private readonly ILogger<GitHubLifecyclePublicationService> _logger = logger ?? NullLogger<GitHubLifecyclePublicationService>.Instance;

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

        using var activity = ActivitySource.StartActivity("GitHubLifecyclePublicationService.PublishReview");
        activity?.SetTag("scm.provider", ScmProvider.GitHub.ToString());
        activity?.SetTag("provider.host", review.Repository.Host.HostBaseUrl);
        activity?.SetTag("review.number", review.Number);
        activity?.SetTag("publication.author.login", author.Login);

        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        var payload = BuildPayload(review, revision, result, author);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GitHubConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/reviews"))
        {
            Content = JsonContent.Create(payload),
        };
        await context.AuthorizeRequestAsync(request, ct);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var responseBody = await ReadDiagnosticBodyAsync(response, ct);
            var safeRepositoryPath = BuildRepositoryPath(review.Repository).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            var safeResponseBody = (responseBody ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            this._logger.LogWarning(
                "GitHub review publication permission failure for repository {RepositoryPath} review {ReviewNumber} with status {StatusCode}. Detail: {Detail}",
                safeRepositoryPath,
                review.Number,
                (int)response.StatusCode,
                safeResponseBody);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(responseBody)
                    ? "GitHub review publication failed because the configured credential no longer has permission to publish review comments."
                    : $"GitHub review publication failed because the configured credential no longer has permission to publish review comments. {responseBody}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await ReadDiagnosticBodyAsync(response, ct);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(responseBody)
                    ? $"GitHub review publication failed with status {(int)response.StatusCode}."
                    : $"GitHub review publication failed with status {(int)response.StatusCode}: {responseBody}");
        }

        // Best-effort capture of the created provider comment ids. The crawler keys retained comments on
        // each review comment's REST numeric id (GraphQL databaseId). The POST /reviews response only
        // carries the review id, so the per-comment ids are fetched via a follow-up read. Any failure
        // here must leave PostedComments empty without disrupting publishing.
        var postedComments = await this.TryCapturePostedCommentsAsync(context, review, response, ct);

        return ReviewCommentPostingDiagnosticsDto.Empty(
                result.Comments.Count + result.CarriedForwardCandidatesSkipped,
                result.CarriedForwardCandidatesSkipped) with
            {
                PostedCount = result.Comments.Count,
                PostedComments = postedComments,
            };
    }

    private async Task<IReadOnlyList<PostedReviewCommentRef>> TryCapturePostedCommentsAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        CodeReviewRef review,
        HttpResponseMessage reviewResponse,
        CancellationToken ct)
    {
        try
        {
            var createdReview = await reviewResponse.Content
                .ReadFromJsonAsync<GitHubReviewResponse>(ct);
            if (createdReview?.Id is not { } reviewId)
            {
                return [];
            }

            var reviewIdText = reviewId.ToString(CultureInfo.InvariantCulture);
            using var commentsRequest = new HttpRequestMessage(
                HttpMethod.Get,
                GitHubConnectionVerifier.BuildApiUri(
                    review.Repository.Host,
                    $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/reviews/{reviewIdText}/comments"));
            await context.AuthorizeRequestAsync(commentsRequest, ct);

            using var commentsResponse = await httpClientFactory.CreateClient("GitHubProvider")
                .SendAsync(commentsRequest, ct);
            if (!commentsResponse.IsSuccessStatusCode)
            {
                return [];
            }

            var comments = await commentsResponse.Content
                .ReadFromJsonAsync<IReadOnlyList<GitHubReviewCommentResponse>>(ct);
            if (comments is null || comments.Count == 0)
            {
                return [];
            }

            return comments
                .Where(comment => comment.Id is not null)
                .Select(comment => new PostedReviewCommentRef(
                    comment.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    reviewIdText,
                    comment.Path,
                    comment.Line))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostedCommentCaptureFailed(this._logger, review.Number, ex);
            return [];
        }
    }

    internal static GitHubReviewRequest BuildPayload(
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity author)
    {
        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"## {author.DisplayName} Review");
        summaryBuilder.AppendLine();
        summaryBuilder.AppendLine(result.Summary);

        var inlineComments = new List<GitHubInlineReviewComment>();
        foreach (var comment in result.Comments)
        {
            if (!string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber.HasValue &&
                comment.LineNumber.Value > 0)
            {
                inlineComments.Add(
                    new GitHubInlineReviewComment(
                        NormalizePath(comment.FilePath),
                        comment.LineNumber.Value,
                        "RIGHT",
                        $"{FormatSeverity(comment.Severity)}: {comment.Message}"));
            }
            else
            {
                summaryBuilder.AppendLine();
                summaryBuilder.AppendLine($"- {FormatSeverity(comment.Severity)}: {comment.Message}");
            }
        }

        return new GitHubReviewRequest(
            revision.HeadSha,
            summaryBuilder.ToString().Trim(),
            "COMMENT",
            inlineComments);
    }

    private static string BuildRepositoryPath(RepositoryRef repository)
    {
        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return $"{Uri.EscapeDataString(repository.OwnerOrNamespace)}/{Uri.EscapeDataString(repositoryName)}";
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

    private static async Task<string?> ReadDiagnosticBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var normalized = body.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000].TrimEnd() + "...";
    }

    [LoggerMessage(
        EventId = 4781,
        Level = LogLevel.Debug,
        Message = "GitHub posted-comment id capture failed for pull request {ReviewNumber}; provenance left empty.")]
    private static partial void LogPostedCommentCaptureFailed(ILogger logger, int reviewNumber, Exception exception);

    private sealed record GitHubReviewResponse([property: JsonPropertyName("id")] long? Id);

    private sealed record GitHubReviewCommentResponse(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("line")] int? Line);

    internal sealed record GitHubReviewRequest(
        [property: JsonPropertyName("commit_id")]
        string CommitId,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("comments")]
        IReadOnlyList<GitHubInlineReviewComment> Comments);

    internal sealed record GitHubInlineReviewComment(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("body")] string Body);
}
