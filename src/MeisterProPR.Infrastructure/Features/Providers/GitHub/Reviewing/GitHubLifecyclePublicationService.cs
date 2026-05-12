// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
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

internal sealed class GitHubLifecyclePublicationService(
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
