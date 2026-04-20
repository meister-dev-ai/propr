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
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabCodeReviewPublicationService(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : ICodeReviewPublicationService
{
    public ScmProvider Provider => ScmProvider.GitLab;

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
        var discussionUri = GitLabConnectionVerifier.BuildApiUri(
            review.Repository.Host,
            $"/projects/{Uri.EscapeDataString(review.Repository.ExternalRepositoryId)}/merge_requests/{review.Number}/discussions");
        var client = httpClientFactory.CreateClient("GitLabProvider");

        var summaryBody = BuildSummaryBody(result, reviewer);
        var inlineComments = result.Comments.Where(IsInlineComment).ToList();
        var inlineRevision = inlineComments.Count > 0
            ? await GetLatestInlineRevisionAsync(client, context.Connection.Secret, review, ct)
            : null;
        var totalDiscussions = inlineComments.Count + (string.IsNullOrWhiteSpace(summaryBody) ? 0 : 1);
        var successfulDiscussionCount = 0;

        if (!string.IsNullOrWhiteSpace(summaryBody))
        {
            await PostDiscussionAsync(
                client,
                context.Connection.Secret,
                discussionUri,
                new GitLabDiscussionRequest(summaryBody, null),
                GitLabDiscussionTarget.Overview(1, totalDiscussions),
                successfulDiscussionCount,
                ct);
            successfulDiscussionCount++;
        }

        foreach (var comment in inlineComments)
        {
            var normalizedPath = NormalizePath(comment.FilePath!);
            await PostDiscussionAsync(
                client,
                context.Connection.Secret,
                discussionUri,
                new GitLabDiscussionRequest(
                    $"{FormatSeverity(comment.Severity)}: {comment.Message}",
                    new GitLabDiscussionPosition(
                        "text",
                        inlineRevision!.BaseSha,
                        inlineRevision.HeadSha,
                        inlineRevision.StartSha,
                        normalizedPath,
                        normalizedPath,
                        comment.LineNumber)),
                GitLabDiscussionTarget.Inline(
                    successfulDiscussionCount + 1,
                    totalDiscussions,
                    normalizedPath,
                    comment.LineNumber!.Value),
                successfulDiscussionCount,
                ct);
            successfulDiscussionCount++;
        }

        return ReviewCommentPostingDiagnosticsDto.Empty(
                result.Comments.Count + result.CarriedForwardCandidatesSkipped,
                result.CarriedForwardCandidatesSkipped) with
        {
            PostedCount = result.Comments.Count,
        };
    }

    private static async Task PostDiscussionAsync(
        HttpClient client,
        string token,
        Uri discussionUri,
        GitLabDiscussionRequest payload,
        GitLabDiscussionTarget target,
        int successfulDiscussionCount,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(discussionUri, token, HttpMethod.Post);
        request.Content = BuildDiscussionContent(payload);

        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await ReadFailureDetailAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                BuildFailureMessage(
                    $"GitLab review publication authentication failed while posting {target.Describe(successfulDiscussionCount)}.",
                    responseBody));
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                BuildFailureMessage(
                    $"GitLab review publication was forbidden while posting {target.Describe(successfulDiscussionCount)}. Ensure the configured GitLab token can create merge request discussions and has the required api scope.",
                    responseBody));
        }

        throw new InvalidOperationException(
            BuildFailureMessage(
                $"GitLab review publication failed while posting {target.Describe(successfulDiscussionCount)} with status {(int)response.StatusCode}.",
                responseBody));
    }

    private static string BuildSummaryBody(ReviewResult result, ReviewerIdentity reviewer)
    {
        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"## {reviewer.DisplayName} Review");
        summaryBuilder.AppendLine();
        summaryBuilder.AppendLine(result.Summary);

        foreach (var comment in result.Comments.Where(comment => !IsInlineComment(comment)))
        {
            summaryBuilder.AppendLine();
            summaryBuilder.AppendLine($"- {FormatSeverity(comment.Severity)}: {comment.Message}");
        }

        return summaryBuilder.ToString().Trim();
    }

    private static bool IsInlineComment(ReviewComment comment)
    {
        return !string.IsNullOrWhiteSpace(comment.FilePath)
               && comment.LineNumber.HasValue
               && comment.LineNumber.Value > 0;
    }

    private static string NormalizePath(string path)
    {
        return path.TrimStart('/');
    }

    private static HttpContent BuildDiscussionContent(GitLabDiscussionRequest payload)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(payload.Body), "body");

        if (payload.Position is not null)
        {
            content.Add(new StringContent(payload.Position.PositionType), "position[position_type]");
            content.Add(new StringContent(payload.Position.BaseSha), "position[base_sha]");
            content.Add(new StringContent(payload.Position.HeadSha), "position[head_sha]");
            content.Add(new StringContent(payload.Position.StartSha), "position[start_sha]");
            content.Add(new StringContent(payload.Position.NewPath), "position[new_path]");
            content.Add(new StringContent(payload.Position.OldPath), "position[old_path]");

            if (payload.Position.NewLine.HasValue)
            {
                content.Add(new StringContent(payload.Position.NewLine.Value.ToString()), "position[new_line]");
            }
        }

        return content;
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

    private static async Task<GitLabInlineRevision> GetLatestInlineRevisionAsync(
        HttpClient client,
        string token,
        CodeReviewRef review,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/projects/{Uri.EscapeDataString(review.Repository.ExternalRepositoryId)}/merge_requests/{review.Number}/versions"),
            token);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab merge request version lookup failed with status {(int)response.StatusCode}.");
        }

        var versions = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabMergeRequestVersionResponse>>(ct)
                       ?? [];

        var latestVersion = versions
            .OrderByDescending(version => version.Id)
            .FirstOrDefault(version =>
                !string.IsNullOrWhiteSpace(version.BaseCommitSha)
                && !string.IsNullOrWhiteSpace(version.HeadCommitSha)
                && !string.IsNullOrWhiteSpace(version.StartCommitSha));

        if (latestVersion is null)
        {
            throw new InvalidOperationException("GitLab merge request version lookup did not return a usable diff version.");
        }

        return new GitLabInlineRevision(
            latestVersion.BaseCommitSha!.Trim(),
            latestVersion.HeadCommitSha!.Trim(),
            latestVersion.StartCommitSha!.Trim());
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

    private sealed record GitLabDiscussionRequest(
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("position")]
        GitLabDiscussionPosition? Position);

    private sealed record GitLabDiscussionPosition(
        [property: JsonPropertyName("position_type")]
        string PositionType,
        [property: JsonPropertyName("base_sha")]
        string BaseSha,
        [property: JsonPropertyName("head_sha")]
        string HeadSha,
        [property: JsonPropertyName("start_sha")]
        string StartSha,
        [property: JsonPropertyName("new_path")]
        string NewPath,
        [property: JsonPropertyName("old_path")]
        string OldPath,
        [property: JsonPropertyName("new_line")]
        int? NewLine);

    private sealed record GitLabInlineRevision(string BaseSha, string HeadSha, string StartSha);

    private sealed record GitLabMergeRequestVersionResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("base_commit_sha")]
        string? BaseCommitSha,
        [property: JsonPropertyName("head_commit_sha")]
        string? HeadCommitSha,
        [property: JsonPropertyName("start_commit_sha")]
        string? StartCommitSha);

    private sealed record GitLabDiscussionTarget(
        int Ordinal,
        int TotalCount,
        string Kind,
        string? FilePath,
        int? LineNumber)
    {
        public static GitLabDiscussionTarget Overview(int ordinal, int totalCount)
        {
            return new GitLabDiscussionTarget(ordinal, totalCount, "overview discussion", null, null);
        }

        public static GitLabDiscussionTarget Inline(int ordinal, int totalCount, string filePath, int lineNumber)
        {
            return new GitLabDiscussionTarget(ordinal, totalCount, "inline discussion", filePath, lineNumber);
        }

        public string Describe(int successfulDiscussionCount)
        {
            var attemptDescription = this.LineNumber.HasValue && !string.IsNullOrWhiteSpace(this.FilePath)
                ? $"{this.Kind} {this.Ordinal}/{this.TotalCount} for {this.FilePath}:L{this.LineNumber.Value}"
                : $"{this.Kind} {this.Ordinal}/{this.TotalCount}";

            return successfulDiscussionCount > 0
                ? $"{attemptDescription} after {successfulDiscussionCount} successful discussion(s)"
                : attemptDescription;
        }
    }
}
