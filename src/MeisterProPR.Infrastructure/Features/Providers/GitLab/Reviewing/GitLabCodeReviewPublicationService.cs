// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterProPR.Infrastructure.Utilities;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabCodeReviewPublicationService(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : ICodeReviewPublicationService
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<ReviewCommentPostingDiagnosticsDto> PublishReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity reviewer,
        CancellationToken ct = default,
        ReviewPublicationContext? publicationContext = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var activity = ActivitySource.StartActivity("GitLabCodeReviewPublicationService.PublishReview");
        activity?.SetTag("scm.provider", ScmProvider.GitLab.ToString());
        activity?.SetTag("provider.host", review.Repository.Host.HostBaseUrl);
        activity?.SetTag("review.number", review.Number);
        activity?.SetTag("publication.author.login", reviewer.Login);

        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        var discussionUri = GitLabConnectionVerifier.BuildApiUri(
            review.Repository.Host,
            $"/projects/{Uri.EscapeDataString(review.Repository.ExternalRepositoryId)}/merge_requests/{review.Number}/discussions");
        var client = httpClientFactory.CreateClient("GitLabProvider");

        var summaryBody = BuildSummaryBody(result, reviewer);
        var inlineComments = result.Comments.Where(IsInlineComment).ToList();
        var nonInlineCommentCount = result.Comments.Count - inlineComments.Count;
        var totalDiscussions = inlineComments.Count + (string.IsNullOrWhiteSpace(summaryBody) ? 0 : 1);

        var state = new PublicationState();

        // Post the summary discussion first — it does not depend on the inline diff revision, so an inline
        // problem cannot cost the summary and a summary rejection cannot abort the inline comments.
        await this.PostSummaryIfNeededAsync(
            client,
            context.Connection.Secret,
            discussionUri,
            summaryBody,
            totalDiscussions,
            state,
            ct);

        // Inline discussions need the merge request's latest diff revision. If that lookup fails, record it
        // as a per-thread failure for each inline comment rather than aborting the whole publish.
        if (inlineComments.Count > 0)
        {
            await this.PostInlineCommentsAsync(
                client,
                context.Connection.Secret,
                review,
                discussionUri,
                inlineComments,
                totalDiscussions,
                state,
                ct);
        }

        var diagnostics = ReviewCommentPostingDiagnosticsDto.Empty(
                result.Comments.Count + result.CarriedForwardCandidatesSkipped,
                result.CarriedForwardCandidatesSkipped) with
            {
                PostedCount = state.PostedInlineCount + (state.SummaryPosted ? nonInlineCommentCount : 0),
                PostedComments = state.PostedComments,
                FailedCount = state.Failures.Count,
                PostingFailures = state.Failures,
            };

        // Every attempted discussion was rejected: surface a publication failure rather than a silent success.
        if (state.PostedDiscussionCount == 0 && state.Failures.Count > 0)
        {
            throw new ReviewCommentPublicationFailedException(diagnostics, state.FailureExceptions);
        }

        return diagnostics;
    }

    private async Task PostSummaryIfNeededAsync(
        HttpClient client,
        string token,
        Uri discussionUri,
        string summaryBody,
        int totalDiscussions,
        PublicationState state,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summaryBody))
        {
            return;
        }

        try
        {
            var captured = await PostDiscussionAsync(
                new GitLabDiscussionPostRequest(
                    client,
                    token,
                    discussionUri,
                    new GitLabDiscussionRequest(summaryBody, null),
                    GitLabDiscussionTarget.Overview(1, totalDiscussions),
                    state.PostedDiscussionCount,
                    null,
                    null),
                ct);
            if (captured is not null)
            {
                state.PostedComments.Add(captured);
            }

            state.PostedDiscussionCount++;
            state.SummaryPosted = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            state.Failures.Add(new ReviewCommentPostingFailure("summary", null, null, ex.Message));
            state.FailureExceptions.Add(ex);
        }
    }

    private async Task PostInlineCommentsAsync(
        HttpClient client,
        string token,
        CodeReviewRef review,
        Uri discussionUri,
        IReadOnlyList<ReviewComment> inlineComments,
        int totalDiscussions,
        PublicationState state,
        CancellationToken ct)
    {
        var (inlineRevision, revisionFailure) = await this.ResolveInlineRevisionAsync(client, token, review, ct);

        foreach (var comment in inlineComments)
        {
            var normalizedPath = NormalizePath(comment.FilePath!);
            if (inlineRevision is null)
            {
                state.Failures.Add(new ReviewCommentPostingFailure("inline", normalizedPath, comment.LineNumber, revisionFailure!.Message));
                continue;
            }

            try
            {
                var captured = await PostDiscussionAsync(
                    new GitLabDiscussionPostRequest(
                        client,
                        token,
                        discussionUri,
                        new GitLabDiscussionRequest(
                            $"{FormatSeverity(comment.Severity)}: {comment.Message}",
                            new GitLabDiscussionPosition(
                                "text",
                                inlineRevision.BaseSha,
                                inlineRevision.HeadSha,
                                inlineRevision.StartSha,
                                normalizedPath,
                                normalizedPath,
                                comment.LineNumber)),
                        GitLabDiscussionTarget.Inline(
                            state.PostedDiscussionCount + 1,
                            totalDiscussions,
                            normalizedPath,
                            comment.LineNumber!.Value),
                        state.PostedDiscussionCount,
                        normalizedPath,
                        comment.LineNumber),
                    ct);
                if (captured is not null)
                {
                    state.PostedComments.Add(captured);
                }

                state.PostedDiscussionCount++;
                state.PostedInlineCount++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                state.Failures.Add(new ReviewCommentPostingFailure("inline", normalizedPath, comment.LineNumber, ex.Message));
                state.FailureExceptions.Add(ex);
            }
        }

        if (revisionFailure is not null)
        {
            state.FailureExceptions.Add(revisionFailure);
        }
    }

    private async Task<(GitLabInlineRevision? Revision, Exception? Failure)> ResolveInlineRevisionAsync(
        HttpClient client,
        string token,
        CodeReviewRef review,
        CancellationToken ct)
    {
        try
        {
            var inlineRevision = await GetLatestInlineRevisionAsync(client, token, review, ct);
            return (inlineRevision, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, ex);
        }
    }

    private sealed class PublicationState
    {
        public int PostedDiscussionCount;
        public int PostedInlineCount;
        public bool SummaryPosted;
        public List<PostedReviewCommentRef> PostedComments { get; } = [];
        public List<ReviewCommentPostingFailure> Failures { get; } = [];
        public List<Exception> FailureExceptions { get; } = [];
    }

    // Posts a single discussion. On success, best-effort parses the created discussion's first note id —
    // the value the thread crawler reports as the comment id — and returns it as provenance. Parsing never
    // affects posting: an unparseable response simply yields a null ref and publishing continues unchanged.
    private static async Task<PostedReviewCommentRef?> PostDiscussionAsync(GitLabDiscussionPostRequest post, CancellationToken ct)
    {
        using var httpRequest = GitLabConnectionVerifier.CreateAuthenticatedRequest(post.DiscussionUri, post.Token, HttpMethod.Post);
        httpRequest.Content = BuildDiscussionContent(post.Payload);

        using var response = await post.Client.SendAsync(httpRequest, ct);
        if (response.IsSuccessStatusCode)
        {
            return await TryCaptureDiscussionRefAsync(response, post.FilePath, post.Line, ct);
        }

        var responseBody = await ReadFailureDetailAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                BuildFailureMessage(
                    $"GitLab review publication authentication failed while posting {post.Target.Describe(post.SuccessfulDiscussionCount)}.",
                    responseBody));
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                BuildFailureMessage(
                    $"GitLab review publication was forbidden while posting {post.Target.Describe(post.SuccessfulDiscussionCount)}. Ensure the configured GitLab token can create merge request discussions and has the required api scope.",
                    responseBody));
        }

        throw new InvalidOperationException(
            BuildFailureMessage(
                $"GitLab review publication failed while posting {post.Target.Describe(post.SuccessfulDiscussionCount)} with status {(int)response.StatusCode}.",
                responseBody));
    }

    private static async Task<PostedReviewCommentRef?> TryCaptureDiscussionRefAsync(
        HttpResponseMessage response,
        string? filePath,
        int? line,
        CancellationToken ct)
    {
        try
        {
            var discussion = await response.Content.ReadFromJsonAsync<GitLabCreatedDiscussionResponse>(ct);
            var firstNoteId = discussion?.Notes?
                .Select(note => note.Id)
                .FirstOrDefault(id => id.HasValue);
            if (firstNoteId is not { } noteId)
            {
                return null;
            }

            return new PostedReviewCommentRef(
                noteId.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(discussion?.Id) ? null : discussion!.Id,
                filePath,
                line);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    private static string BuildSummaryBody(ReviewResult result, ReviewerIdentity author)
    {
        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"## {author.DisplayName} Review");
        summaryBuilder.AppendLine();
        summaryBuilder.AppendLine(result.Summary);

        foreach (var comment in result.Comments.Where(comment => !IsInlineComment(comment)))
        {
            summaryBuilder.AppendLine();
            summaryBuilder.AppendLine($"- {FormatSeverity(comment.Severity)}: {comment.Message}");
        }

        ContextBudgetSummarySections.Append(summaryBuilder, result);

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
        return path.Trim().TrimStart('/');
    }

    private static HttpContent BuildDiscussionContent(GitLabDiscussionRequest payload)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(HtmlSanitizer.Sanitize(payload.Body)), "body");

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

    private sealed record GitLabDiscussionPostRequest(
        HttpClient Client,
        string Token,
        Uri DiscussionUri,
        GitLabDiscussionRequest Payload,
        GitLabDiscussionTarget Target,
        int SuccessfulDiscussionCount,
        string? FilePath,
        int? Line);

    private sealed record GitLabCreatedDiscussionResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("notes")] IReadOnlyList<GitLabCreatedNoteResponse>? Notes);

    private sealed record GitLabCreatedNoteResponse([property: JsonPropertyName("id")] long? Id);

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
