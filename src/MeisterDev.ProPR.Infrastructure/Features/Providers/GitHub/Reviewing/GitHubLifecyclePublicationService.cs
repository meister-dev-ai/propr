// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed partial class GitHubLifecyclePublicationService(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    IPostedCommentComposer composer,
    ILogger<GitHubLifecyclePublicationService>? logger = null)
{
    private const int MaximumCommentBodyCharacters = 65_536;

    // Read across pages from pageInfo, matching the thread read path. A pull request past a hundred threads
    // used to leave the overflow without a thread id, which cost auto-resolution on those findings.
    private const string ReviewThreadIdsQuery =
        "query ReviewThreadIds($owner: String!, $name: String!, $pullRequestNumber: Int!, $after: String) { repository(owner: $owner, name: $name) { pullRequest(number: $pullRequestNumber) { reviewThreads(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { id comments(first: 100) { nodes { databaseId } } } } } } }";

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
        var payload = BuildPayload(
            review, revision, result, author, composer, publicationContext?.CurrentHeadSha,
            publicationContext?.FindingBadgeStyle ?? FindingBadgeStyle.ShieldsIo);
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
        // The review-thread node ids need a second follow-up read: GitHub exposes review threads only
        // through GraphQL, and neither the create-review response nor the REST comment payload names the
        // thread a comment opened.
        var postedComments = await this.TryCapturePostedCommentsAsync(context, review, response, result, payload, ct);

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
        ReviewResult result,
        GitHubReviewRequest payload,
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

            // Read across pages: without a page size this listing takes GitHub's default of thirty, so a
            // review posting more comments than that recorded provenance for the first thirty only and lost
            // auto-resolution on the rest.
            var comments = await ProviderRestPager.LoadAllAsync(
                (page, pageSize, pageCt) => this.GetReviewCommentPageAsync(
                    context,
                    review,
                    reviewIdText,
                    page,
                    pageSize,
                    pageCt),
                comment => comment.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                $"GitHub's comment listing for review {reviewIdText}",
                ct);
            if (comments.Count == 0)
            {
                return [];
            }

            var threadIdsByCommentId = await this.TryResolveReviewThreadIdsAsync(context, review, ct);

            var originals = result.Comments.Select((finding, ordinal) => (Finding: finding, Ordinal: ordinal))
                .Where(item => !string.IsNullOrWhiteSpace(item.Finding.FilePath) && item.Finding.LineNumber is > 0).ToArray();
            return comments.Where(comment => comment.Id is not null).Select(comment =>
            {
                var matches = payload.Comments.Select((sent, index) => (Sent: sent, Index: index))
                    .Where(item => item.Sent.Path == comment.Path && item.Sent.Line == comment.Line
                                                                  && (comment.Body is null || item.Sent.Body == comment.Body)).Take(2).ToArray();
                var original = matches.Length == 1 ? originals[matches[0].Index] : default;
                return new PostedReviewCommentRef(
                    comment.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    threadIdsByCommentId.GetValueOrDefault(comment.Id.Value), original.Finding?.FilePath ?? comment.Path,
                    original.Finding?.LineNumber ?? comment.Line)
                {
                    FindingOrdinal = original.Finding is not null ? original.Ordinal : null,
                };
            }).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostedCommentCaptureFailed(this._logger, review.Number, ex);
            return [];
        }
    }

    private async Task<ProviderRestPager.RestPage<GitHubReviewCommentResponse>> GetReviewCommentPageAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        CodeReviewRef review,
        string reviewIdText,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            GitHubConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/reviews/{reviewIdText}/comments",
                page <= 1
                    ? $"per_page={pageSize.ToString(CultureInfo.InvariantCulture)}"
                    : $"per_page={pageSize.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}"));
        await context.AuthorizeRequestAsync(request, ct);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub review-comment listing failed with status {(int)response.StatusCode}.");
        }

        var comments = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubReviewCommentResponse>>(ct)
                       ?? [];

        return new ProviderRestPager.RestPage<GitHubReviewCommentResponse>(
            comments,
            GitHubPaginationHeaders.ReadGitHubHasMore(response));
    }

    /// <summary>
    ///     Maps each of the pull request's review comments to the node id of the thread it belongs to.
    ///     GitHub's review threads exist only in GraphQL: the create-review response names the review, and the
    ///     REST comment payload names neither its thread nor anything a thread can be derived from, so the
    ///     mapping has to be read back. A comment's REST id is its GraphQL databaseId, and that joins the
    ///     two reads.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, string>> TryResolveReviewThreadIdsAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        CodeReviewRef review,
        CancellationToken ct)
    {
        try
        {
            var (owner, name) = BuildRepositoryCoordinates(review.Repository);

            // A page this traversal could not read abandons the whole mapping rather than keying writes on the
            // part that arrived, on the same reasoning as a refusal: ids from an incomplete read are not safe
            // to send a later write at.
            var abandoned = false;
            var threads = await ProviderCursorPager.LoadAllAsync(
                async (cursor, pageCt) =>
                {
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        GitHubConnectionVerifier.BuildGraphQlUri(review.Repository.Host))
                    {
                        Content = JsonContent.Create(
                            new
                            {
                                query = ReviewThreadIdsQuery,
                                variables = new
                                {
                                    owner,
                                    name,
                                    pullRequestNumber = review.Number,
                                    after = cursor,
                                },
                            }),
                    };
                    await context.AuthorizeRequestAsync(request, pageCt);

                    using var response = await httpClientFactory.CreateClient("GitHubProvider")
                        .SendAsync(request, pageCt);
                    if (!response.IsSuccessStatusCode)
                    {
                        LogReviewThreadIdLookupUnavailable(this._logger, review.Number, (int)response.StatusCode);
                        abandoned = true;
                        return new ProviderCursorPager.CursorPage<GitHubReviewThreadNode>([], false, null);
                    }

                    var payload = await response.Content.ReadFromJsonAsync<GitHubThreadLookupResponse>(pageCt);

                    // GitHub answers a refused query with HTTP 200 and an errors array, and a partial refusal
                    // with a populated data section beside it, so a status-only check would trust a traversal
                    // that reported a problem. Ids read out of one are not safe to key a later write on, so the
                    // lookup is discarded rather than half believed.
                    if (payload?.Errors is { Count: > 0 })
                    {
                        LogReviewThreadIdLookupRefused(this._logger, review.Number);
                        abandoned = true;
                        return new ProviderCursorPager.CursorPage<GitHubReviewThreadNode>([], false, null);
                    }

                    var connection = payload?.Data?.Repository?.PullRequest?.ReviewThreads;

                    return new ProviderCursorPager.CursorPage<GitHubReviewThreadNode>(
                        connection?.Nodes ?? [],
                        connection?.PageInfo?.HasNextPage ?? false,
                        connection?.PageInfo?.EndCursor);
                },
                $"GitHub's review-thread id listing for pull request {review.Number}",
                ct);

            if (abandoned || threads.Count == 0)
            {
                return ReadOnlyDictionary<long, string>.Empty;
            }

            var threadIdsByCommentId = new Dictionary<long, string>();
            foreach (var thread in threads.Where(thread => !string.IsNullOrWhiteSpace(thread.Id)))
            {
                foreach (var comment in thread.Comments?.Nodes ?? [])
                {
                    if (comment.DatabaseId is { } databaseId)
                    {
                        threadIdsByCommentId[databaseId] = thread.Id!;
                    }
                }
            }

            return threadIdsByCommentId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A comment recorded without a thread id loses auto-resolution for that finding. A comment
            // recorded against the wrong id would send a later write at an object that is not a thread, so
            // absent beats invented.
            LogReviewThreadIdLookupFailed(this._logger, review.Number, ex);
            return ReadOnlyDictionary<long, string>.Empty;
        }
    }

    internal static GitHubReviewRequest BuildPayload(
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity author,
        IPostedCommentComposer composer,
        string? currentHeadSha = null, FindingBadgeStyle badgeStyle = FindingBadgeStyle.ShieldsIo)
    {
        ArgumentNullException.ThrowIfNull(composer);

        var bodyBudget = Math.Max(0, MaximumCommentBodyCharacters - composer.AppendRendered(string.Empty, GitHubFindingCommentRenderer.RenderText).Length - 2);
        var inlineComments = new List<GitHubInlineReviewComment>();
        foreach (var comment in result.Comments)
        {
            if (!string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber.HasValue &&
                comment.LineNumber.Value > 0)
            {
                var applicable = currentHeadSha == revision.HeadSha && ReviewSuggestedFixRenderer.CanApply(comment, currentHeadSha);
                var fix = applicable ? comment.SuggestedFix : null;
                var body = FitFindingMetadata(GitHubFindingCommentRenderer.Format(comment, applicable, badgeStyle: badgeStyle), bodyBudget);
                if (body.Length > bodyBudget && comment.SuggestedFix is not null)
                {
                    body = FitFindingMetadata(
                        GitHubFindingCommentRenderer.Format(comment with { SuggestedFix = null }, badgeStyle: badgeStyle),
                        bodyBudget);
                    fix = null;
                }

                body = composer.AppendRendered(ReviewFindingTextFormatter.Truncate(body, bodyBudget), GitHubFindingCommentRenderer.RenderText);

                inlineComments.Add(
                    new GitHubInlineReviewComment(
                        NormalizePath(comment.FilePath),
                        fix?.EndLine ?? comment.LineNumber.Value,
                        "RIGHT",
                        body,
                        fix is not null && fix.StartLine != fix.EndLine ? fix.StartLine : null,
                        fix is not null && fix.StartLine != fix.EndLine ? "RIGHT" : null));
            }
        }

        var summary = BuildSummaryBody(result, author, includeSuggestedFixes: true, badgeStyle, bodyBudget);
        if (summary.Length > bodyBudget)
        {
            summary = BuildSummaryBody(result, author, includeSuggestedFixes: false, badgeStyle, bodyBudget);
        }

        summary = composer.AppendRendered(ReviewFindingTextFormatter.Truncate(summary, bodyBudget), GitHubFindingCommentRenderer.RenderText);

        return new GitHubReviewRequest(
            revision.HeadSha,
            summary,
            "COMMENT",
            inlineComments);
    }

    private static string FitFindingMetadata(string body, int maximumBodyLength)
    {
        if (body.Length <= maximumBodyLength)
        {
            return body;
        }

        var withoutHeader = ReviewFindingTextFormatter.LimitBadgeHeader(body, 0);
        return ReviewFindingTextFormatter.LimitBadgeHeader(body, Math.Max(0, maximumBodyLength - withoutHeader.Length));
    }

    private static string BuildSummaryBody(
        ReviewResult result, ReviewerIdentity author, bool includeSuggestedFixes, FindingBadgeStyle badgeStyle, int maximumBodyLength)
    {
        var fileless = result.Comments.Where(comment => string.IsNullOrWhiteSpace(comment.FilePath) || comment.LineNumber is not > 0).ToArray();
        var rendered = fileless.Distinct().ToDictionary(
            comment => comment,
            comment => GitHubFindingCommentRenderer.Format(includeSuggestedFixes ? comment : comment with { SuggestedFix = null }, badgeStyle: badgeStyle));
        var summary = FormatSummary(result, author, comment => rendered[comment]);
        if (summary.Length <= maximumBodyLength)
        {
            return summary;
        }

        // Only formatter-owned prefixes are reduced; badge examples in descriptions remain unchanged.
        var withoutHeaders = FormatSummary(result, author, comment => ReviewFindingTextFormatter.LimitBadgeHeader(rendered[comment], 0));
        if (withoutHeaders.Length <= maximumBodyLength && fileless.Length > 0)
        {
            var headerBudget = (maximumBodyLength - withoutHeaders.Length) / fileless.Length;
            return FormatSummary(result, author, comment => ReviewFindingTextFormatter.LimitBadgeHeader(rendered[comment], headerBudget));
        }

        if (includeSuggestedFixes)
        {
            return withoutHeaders;
        }

        if (fileless.Length == 0)
        {
            return ReviewFindingTextFormatter.Truncate(FormatSummary(result, author, _ => string.Empty), maximumBodyLength);
        }

        var shellBudget = maximumBodyLength / 2;
        var shell = FormatSummary(result, author, _ => string.Empty, shellBudget);
        if (shell.Length >= maximumBodyLength)
        {
            return ReviewFindingTextFormatter.Truncate(shell, maximumBodyLength);
        }

        var findingBudget = (maximumBodyLength - shell.Length) / fileless.Length;
        return FormatSummary(
            result, author, comment => ReviewFindingTextFormatter.Truncate(FitFindingMetadata(rendered[comment], findingBudget), findingBudget), shellBudget);
    }

    private static string FormatSummary(
        ReviewResult result, ReviewerIdentity author, Func<ReviewComment, string> findingRenderer, int? maximumShellLength = null)
    {
        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"## {GitHubFindingCommentRenderer.RenderText(author.DisplayName.ReplaceLineEndings(" "))} Review");
        summaryBuilder.AppendLine();
        summaryBuilder.Append(ReviewFindingPriority.FormatLead(result, GitHubFindingCommentRenderer.RenderText));
        summaryBuilder.AppendLine(ReviewFindingTextFormatter.CloseOpenFences(GitHubFindingCommentRenderer.RenderText(result.Summary)));
        summaryBuilder.AppendLine();
        var summaryResult = result;
        var sourcesByDisplay = new Dictionary<ReviewComment, ReviewComment>(ReferenceEqualityComparer.Instance);
        if (maximumShellLength.HasValue)
        {
            var comments = result.Comments.Select(comment =>
            {
                if (comment.LineNumber is > 0 || comment.FilePath is not { Length: > 160 } path)
                {
                    return comment;
                }

                var length = char.IsHighSurrogate(path[159]) ? 159 : 160;
                var display = comment.WithAnchorAndSeverity(path[..length] + "…", comment.LineNumber, comment.Severity);
                sourcesByDisplay.Add(display, comment);
                return display;
            }).ToArray();
            summaryResult = result with { Comments = comments };
        }

        var findings = ReviewSummaryTextFormatter.Format(
            summaryResult, includeFilelessFindingDetails: true,
            findingRenderer: comment => findingRenderer(sourcesByDisplay.GetValueOrDefault(comment, comment)),
            renderText: GitHubFindingCommentRenderer.RenderText);
        var contextBuilder = new StringBuilder();
        ContextBudgetSummarySections.Append(contextBuilder, result, GitHubFindingCommentRenderer.RenderText);
        if (maximumShellLength is { } shellBudget)
        {
            var emptyFindings = ReviewSummaryTextFormatter.Format(
                summaryResult, includeFilelessFindingDetails: true, findingRenderer: _ => string.Empty, renderText: GitHubFindingCommentRenderer.RenderText);
            var textBudget = Math.Max(0, shellBudget - emptyFindings.Length - 4);
            if (summaryBuilder.Length + contextBuilder.Length > textBudget)
            {
                var contextBudget = Math.Min(contextBuilder.Length, textBudget / 2);
                var conclusion = ReviewFindingTextFormatter.Truncate(summaryBuilder.ToString().TrimEnd(), textBudget - contextBudget);
                var context = ReviewFindingTextFormatter.Truncate(contextBuilder.ToString().Trim(), contextBudget);
                return (conclusion + "\n\n" + findings + "\n\n" + context).Trim();
            }
        }

        summaryBuilder.AppendLine(findings).Append(contextBuilder);
        return summaryBuilder.ToString().Trim();
    }

    private static string BuildRepositoryPath(RepositoryRef repository)
    {
        var (owner, name) = BuildRepositoryCoordinates(repository);
        return $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
    }

    // GraphQL takes owner and name as separate variables, so they are carried unescaped here and escaped
    // only where they are spliced into a REST path.
    private static (string Owner, string Name) BuildRepositoryCoordinates(RepositoryRef repository)
    {
        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return (repository.OwnerOrNamespace, repositoryName);
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/');
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

    [LoggerMessage(
        EventId = 4782,
        Level = LogLevel.Debug,
        Message = "GitHub review-thread id lookup for pull request {ReviewNumber} answered with status {StatusCode}; posted comments carry no thread id.")]
    private static partial void LogReviewThreadIdLookupUnavailable(ILogger logger, int reviewNumber, int statusCode);

    [LoggerMessage(
        EventId = 4783,
        Level = LogLevel.Debug,
        Message = "GitHub refused the review-thread id lookup for pull request {ReviewNumber}; posted comments carry no thread id.")]
    private static partial void LogReviewThreadIdLookupRefused(ILogger logger, int reviewNumber);

    [LoggerMessage(
        EventId = 4784,
        Level = LogLevel.Debug,
        Message = "GitHub review-thread id lookup failed for pull request {ReviewNumber}; posted comments carry no thread id.")]
    private static partial void LogReviewThreadIdLookupFailed(ILogger logger, int reviewNumber, Exception exception);

    private sealed record GitHubReviewResponse([property: JsonPropertyName("id")] long? Id);

    private sealed record GitHubThreadLookupResponse(
        [property: JsonPropertyName("data")] GitHubThreadLookupData? Data,
        [property: JsonPropertyName("errors")] IReadOnlyList<GitHubThreadLookupError>? Errors);

    private sealed record GitHubThreadLookupError(
        [property: JsonPropertyName("message")]
        string? Message);

    private sealed record GitHubThreadLookupData(
        [property: JsonPropertyName("repository")]
        GitHubThreadLookupRepository? Repository);

    private sealed record GitHubThreadLookupRepository(
        [property: JsonPropertyName("pullRequest")]
        GitHubThreadLookupPullRequest? PullRequest);

    private sealed record GitHubThreadLookupPullRequest(
        [property: JsonPropertyName("reviewThreads")]
        GitHubReviewThreadConnection? ReviewThreads);

    private sealed record GitHubReviewThreadConnection(
        [property: JsonPropertyName("nodes")] IReadOnlyList<GitHubReviewThreadNode>? Nodes,
        [property: JsonPropertyName("pageInfo")]
        GitHubPageInfo? PageInfo);

    private sealed record GitHubPageInfo(
        [property: JsonPropertyName("hasNextPage")]
        bool HasNextPage,
        [property: JsonPropertyName("endCursor")]
        string? EndCursor);

    private sealed record GitHubReviewThreadNode(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("comments")]
        GitHubThreadCommentConnection? Comments);

    private sealed record GitHubThreadCommentConnection([property: JsonPropertyName("nodes")] IReadOnlyList<GitHubThreadCommentNode>? Nodes);

    private sealed record GitHubThreadCommentNode(
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId);

    private sealed record GitHubReviewCommentResponse(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("line")] int? Line,
        [property: JsonPropertyName("body")] string? Body = null);

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
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("start_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? StartLine = null,
        [property: JsonPropertyName("start_side"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? StartSide = null);
}
