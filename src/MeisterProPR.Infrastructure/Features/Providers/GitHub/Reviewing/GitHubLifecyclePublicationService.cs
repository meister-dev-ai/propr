// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubLifecyclePublicationService(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory)
{
    public async Task<ReviewCommentPostingDiagnosticsDto> PublishReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity reviewer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        var payload = BuildPayload(review, revision, result, reviewer);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GitHubConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/reviews"))
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Connection.Secret);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("GitHub review publication authentication failed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub review publication failed with status {(int)response.StatusCode}.");
        }

        return ReviewCommentPostingDiagnosticsDto.Empty(
                result.Comments.Count + result.CarriedForwardCandidatesSkipped,
                result.CarriedForwardCandidatesSkipped) with
        {
            PostedCount = result.Comments.Count,
        };
    }

    internal static GitHubReviewRequest BuildPayload(
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity reviewer)
    {
        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"## {reviewer.DisplayName} Review");
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
            inlineComments.Count == 0 ? null : inlineComments);
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
        return path.TrimStart('/');
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

    internal sealed record GitHubReviewRequest(
        [property: JsonPropertyName("commit_id")]
        string CommitId,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("comments")]
        IReadOnlyList<GitHubInlineReviewComment>? Comments);

    internal sealed record GitHubInlineReviewComment(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("body")] string Body);
}
