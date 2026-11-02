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

    // GitHub lists at most 300 changed files in a comparison. A listing of that size may leave files out.
    private const int ComparisonFileLimit = 300;

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
        var badgeStyle = publicationContext?.FindingBadgeStyle ?? FindingBadgeStyle.ShieldsIo;
        var diffLines = await this.TryReadDiffLinesAsync(context, review, revision, ct);
        var payload = BuildPayload(review, revision, result, author, composer, publicationContext?.CurrentHeadSha, badgeStyle, diffLines);
        if (payload.OutsideDiffCount > 0)
        {
            LogFindingsOutsideDiff(this._logger, review.Number, payload.OutsideDiffCount);
        }

        var response = await this.SendReviewAsync(context, review, payload, ct);
        try
        {
            // GitHub refuses the whole review when one inline comment names a line it cannot resolve against the
            // diff, and creates nothing. The diff read above prevents that in the common case. A push between that
            // read and this request, or a diff the read could not obtain, still ends here, so the review is sent
            // once more with every finding in the summary and the customer receives the result.
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity && payload.Comments.Count > 0)
            {
                var refusal = await ReadDiagnosticBodyAsync(response, ct);
                LogInlineCommentsRefused(this._logger, review.Number, payload.Comments.Count, Sanitize(refusal));
                response.Dispose();
                payload = BuildPayload(review, revision, result, author, composer, publicationContext?.CurrentHeadSha, badgeStyle, GitHubDiffLines.None);
                response = await this.SendReviewAsync(context, review, payload, ct);
            }

            await this.ThrowIfRefusedAsync(review, response, ct);

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
                    PlacedInSummaryCount = payload.OutsideDiffCount,
                };
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendReviewAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        CodeReviewRef review,
        GitHubReviewRequest payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GitHubConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/reviews"))
        {
            Content = JsonContent.Create(payload),
        };
        await context.AuthorizeRequestAsync(request, ct);
        return await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
    }

    private async Task ThrowIfRefusedAsync(CodeReviewRef review, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var responseBody = await ReadDiagnosticBodyAsync(response, ct);
            this._logger.LogWarning(
                "GitHub review publication permission failure for repository {RepositoryPath} review {ReviewNumber} with status {StatusCode}. Detail: {Detail}",
                Sanitize(BuildRepositoryPath(review.Repository)),
                review.Number,
                (int)response.StatusCode,
                Sanitize(responseBody));
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
    }

    /// <summary>
    ///     Reads the lines of the reviewed revision's diff that accept a review comment. GitHub resolves a comment
    ///     against the diff of the commit the review names, so the comparison of the revision's base and head is
    ///     read, not the pull request's current file list, which follows later pushes. Returns
    ///     <see langword="null" /> when the comparison cannot be read; every finding is then sent inline as before,
    ///     and a refusal falls back to the summary.
    /// </summary>
    private async Task<GitHubDiffLines?> TryReadDiffLinesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        CodeReviewRef review,
        ReviewRevision revision,
        CancellationToken ct)
    {
        try
        {
            // The page size limits the commits of the answer. GitHub lists the changed files on the first page only.
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                GitHubConnectionVerifier.BuildApiUri(
                    review.Repository.Host,
                    $"/repos/{BuildRepositoryPath(review.Repository)}/compare/{Uri.EscapeDataString(revision.BaseSha)}...{Uri.EscapeDataString(revision.HeadSha)}",
                    "per_page=1"));
            await context.AuthorizeRequestAsync(request, ct);

            using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogDiffUnavailable(this._logger, review.Number, (int)response.StatusCode, null);
                return null;
            }

            var comparison = await response.Content.ReadFromJsonAsync<GitHubComparisonResponse>(ct);
            if (comparison?.Files is not { } files)
            {
                LogDiffUnavailable(this._logger, review.Number, (int)response.StatusCode, null);
                return null;
            }

            return GitHubDiffLines.FromFiles(
                files.Select(file => (file.FileName ?? string.Empty, file.Patch)),
                complete: files.Count < ComparisonFileLimit);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogDiffUnavailable(this._logger, review.Number, null, ex);
            return null;
        }
    }

    private async Task<IReadOnlyList<PostedReviewCommentRef>> TryCapturePostedCommentsAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        CodeReviewRef review,
        HttpResponseMessage reviewResponse,
        ReviewResult result,
        GitHubReviewRequest payload,
        CancellationToken ct)
    {
        // A review without inline comments created no comment to read back, and the pull request's comment listing
        // would be read across all its pages for nothing.
        if (payload.Comments.Count == 0)
        {
            return [];
        }

        try
        {
            var createdReview = await reviewResponse.Content
                .ReadFromJsonAsync<GitHubReviewResponse>(ct);
            if (createdReview?.Id is not { } reviewId)
            {
                return [];
            }

            // GitHub's listing of one review's comments carries each comment's diff position and no line, so a
            // comment could never be matched to the finding it was sent for. The pull request's comment listing
            // carries the line and the review each comment belongs to, so the comments are read from there and
            // kept when they belong to the review just created. The read spans pages: without a page size the
            // listing takes GitHub's default of thirty.
            var comments = (await ProviderRestPager.LoadAllAsync(
                    (page, pageSize, pageCt) => this.GetPullRequestCommentPageAsync(
                        context,
                        review,
                        page,
                        pageSize,
                        pageCt),
                    comment => comment.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    $"GitHub's review-comment listing for pull request {review.Number}",
                    ct))
                .Where(comment => comment.ReviewId == reviewId)
                .ToList();
            if (comments.Count == 0)
            {
                return [];
            }

            var threadIdsByCommentId = await this.TryResolveReviewThreadIdsAsync(context, review, ct);

            // Each sent comment is matched once, in the order GitHub created the comments.
            var claimed = new HashSet<int>();
            return comments.Where(comment => comment.Id is not null).OrderBy(comment => comment.Id).Select(comment =>
            {
                // The sent comments name lines of the reviewed revision. GitHub reports that line as the original line,
                // and the current line follows later pushes, so the original line is compared first.
                var line = comment.OriginalLine ?? comment.Line;
                var matches = payload.Comments.Select((sent, index) => (Sent: sent, Index: index))
                    .Where(item => !claimed.Contains(item.Index) && item.Sent.Path == comment.Path && item.Sent.Line == line).ToArray();

                // The body tells apart two comments on the same line. It is not compared otherwise, so that a change
                // GitHub makes to a stored body does not cost the match. Comments that still match after the
                // comparison carry the same text and stand for interchangeable findings, so the first one is taken.
                if (matches.Length > 1)
                {
                    matches = comment.Body is null ? [] : matches.Where(item => item.Sent.Body == comment.Body).Take(1).ToArray();
                }

                // Only the findings placed inline were sent as comments, so the sent comment's position maps to the
                // finding through the ordinals recorded when the payload was built.
                int? ordinal = null;
                if (matches.Length == 1)
                {
                    claimed.Add(matches[0].Index);
                    ordinal = payload.InlineFindingOrdinals[matches[0].Index];
                }

                var finding = ordinal is { } index ? result.Comments[index] : null;
                return new PostedReviewCommentRef(
                    comment.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    threadIdsByCommentId.GetValueOrDefault(comment.Id.Value), finding?.FilePath ?? comment.Path,
                    finding?.LineNumber ?? line)
                {
                    FindingOrdinal = ordinal,
                };
            }).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostedCommentCaptureFailed(this._logger, review.Number, ex);
            return [];
        }
    }

    private async Task<ProviderRestPager.RestPage<GitHubReviewCommentResponse>> GetPullRequestCommentPageAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        CodeReviewRef review,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            GitHubConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/comments",
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
        string? currentHeadSha = null, FindingBadgeStyle badgeStyle = FindingBadgeStyle.ShieldsIo,
        GitHubDiffLines? diffLines = null)
    {
        ArgumentNullException.ThrowIfNull(composer);

        var bodyBudget = Math.Max(0, MaximumCommentBodyCharacters - composer.AppendRendered(string.Empty, GitHubFindingCommentRenderer.RenderText).Length - 2);
        var inlineComments = new List<GitHubInlineReviewComment>();
        var inlineOrdinals = new List<int>();
        var inlineFindings = new HashSet<ReviewComment>();
        for (var ordinal = 0; ordinal < result.Comments.Count; ordinal++)
        {
            var comment = result.Comments[ordinal];
            if (string.IsNullOrWhiteSpace(comment.FilePath) || comment.LineNumber is not > 0)
            {
                continue;
            }

            var path = NormalizePath(comment.FilePath);
            var applicable = currentHeadSha == revision.HeadSha && ReviewSuggestedFixRenderer.CanApply(comment, currentHeadSha);

            // An applicable replacement names a line range, and GitHub accepts the range when its first and its last
            // line are lines of the diff; the two may lie in different hunks. A range reaching outside the diff is
            // shown as ordinary code on the finding's own line.
            if (applicable && diffLines is not null && comment.SuggestedFix is { } range
                && !(diffLines.Accepts(path, range.StartLine) && diffLines.Accepts(path, range.EndLine)))
            {
                applicable = false;
            }

            var fix = applicable ? comment.SuggestedFix : null;
            var body = FitFindingMetadata(GitHubFindingCommentRenderer.Format(comment, applicable, badgeStyle: badgeStyle), bodyBudget);
            if (body.Length > bodyBudget && comment.SuggestedFix is not null)
            {
                body = FitFindingMetadata(
                    GitHubFindingCommentRenderer.Format(comment with { SuggestedFix = null }, badgeStyle: badgeStyle),
                    bodyBudget);
                fix = null;
            }

            var line = fix?.EndLine ?? comment.LineNumber.Value;
            if (diffLines is not null && !diffLines.Accepts(path, line))
            {
                // GitHub would refuse the whole review over this comment, so the summary lists the finding in full.
                continue;
            }

            body = composer.AppendRendered(ReviewFindingTextFormatter.Truncate(body, bodyBudget), GitHubFindingCommentRenderer.RenderText);

            inlineComments.Add(
                new GitHubInlineReviewComment(
                    path,
                    line,
                    "RIGHT",
                    body,
                    fix is not null && fix.StartLine != fix.EndLine ? fix.StartLine : null,
                    fix is not null && fix.StartLine != fix.EndLine ? "RIGHT" : null));
            inlineOrdinals.Add(ordinal);
            inlineFindings.Add(comment);
        }

        var outsideDiffCount = result.Comments.Count(comment =>
            !string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber is > 0 && !inlineFindings.Contains(comment));
        var summary = BuildSummaryBody(result, author, includeSuggestedFixes: true, badgeStyle, bodyBudget, inlineFindings, outsideDiffCount);
        if (summary.Length > bodyBudget)
        {
            summary = BuildSummaryBody(result, author, includeSuggestedFixes: false, badgeStyle, bodyBudget, inlineFindings, outsideDiffCount);
        }

        summary = composer.AppendRendered(ReviewFindingTextFormatter.Truncate(summary, bodyBudget), GitHubFindingCommentRenderer.RenderText);

        return new GitHubReviewRequest(
            revision.HeadSha,
            summary,
            "COMMENT",
            inlineComments)
        {
            InlineFindingOrdinals = inlineOrdinals,
            OutsideDiffCount = outsideDiffCount,
        };
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
        ReviewResult result,
        ReviewerIdentity author,
        bool includeSuggestedFixes,
        FindingBadgeStyle badgeStyle,
        int maximumBodyLength,
        IReadOnlySet<ReviewComment> inlineFindings,
        int outsideDiffCount)
    {
        // Every finding without an inline comment appears in full: findings without a file or line, and findings
        // on lines outside the diff.
        var fileless = result.Comments.Where(comment => !inlineFindings.Contains(comment)).ToArray();
        var rendered = fileless.Distinct().ToDictionary(
            comment => comment,
            comment => GitHubFindingCommentRenderer.Format(includeSuggestedFixes ? comment : comment with { SuggestedFix = null }, badgeStyle: badgeStyle));
        var layout = new SummaryLayout(inlineFindings, outsideDiffCount);
        var summary = FormatSummary(result, author, layout, comment => rendered[comment]);
        if (summary.Length <= maximumBodyLength)
        {
            return summary;
        }

        // Only formatter-owned prefixes are reduced; badge examples in descriptions remain unchanged.
        var withoutHeaders = FormatSummary(result, author, layout, comment => ReviewFindingTextFormatter.LimitBadgeHeader(rendered[comment], 0));
        if (withoutHeaders.Length <= maximumBodyLength && fileless.Length > 0)
        {
            var headerBudget = (maximumBodyLength - withoutHeaders.Length) / fileless.Length;
            return FormatSummary(result, author, layout, comment => ReviewFindingTextFormatter.LimitBadgeHeader(rendered[comment], headerBudget));
        }

        if (includeSuggestedFixes)
        {
            return withoutHeaders;
        }

        if (fileless.Length == 0)
        {
            return ReviewFindingTextFormatter.Truncate(FormatSummary(result, author, layout, _ => string.Empty), maximumBodyLength);
        }

        var shellBudget = maximumBodyLength / 2;
        var shell = FormatSummary(result, author, layout, _ => string.Empty, shellBudget);
        if (shell.Length >= maximumBodyLength)
        {
            return ReviewFindingTextFormatter.Truncate(shell, maximumBodyLength);
        }

        var findingBudget = (maximumBodyLength - shell.Length) / fileless.Length;
        return FormatSummary(
            result, author, layout, comment => ReviewFindingTextFormatter.Truncate(FitFindingMetadata(rendered[comment], findingBudget), findingBudget),
            shellBudget);
    }

    private static string FormatSummary(
        ReviewResult result,
        ReviewerIdentity author,
        SummaryLayout layout,
        Func<ReviewComment, string> findingRenderer,
        int? maximumShellLength = null)
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
            renderText: GitHubFindingCommentRenderer.RenderText,
            hasInlineComment: layout.HasInlineComment,
            findingsNote: layout.OutsideDiffNote);
        var contextBuilder = new StringBuilder();
        ContextBudgetSummarySections.Append(contextBuilder, result, GitHubFindingCommentRenderer.RenderText);
        if (maximumShellLength is { } shellBudget)
        {
            var emptyFindings = ReviewSummaryTextFormatter.Format(
                summaryResult, includeFilelessFindingDetails: true, findingRenderer: _ => string.Empty, renderText: GitHubFindingCommentRenderer.RenderText,
                hasInlineComment: layout.HasInlineComment, findingsNote: layout.OutsideDiffNote);
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

    private static string Sanitize(string? text)
    {
        return (text ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
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

    [LoggerMessage(
        EventId = 4785,
        Level = LogLevel.Warning,
        Message =
            "GitHub's comparison for pull request {ReviewNumber} could not be read (status {StatusCode}); every finding is sent inline and a refusal falls back to the summary.")]
    private static partial void LogDiffUnavailable(ILogger logger, int reviewNumber, int? statusCode, Exception? exception);

    [LoggerMessage(
        EventId = 4786,
        Level = LogLevel.Information,
        Message = "{FindingCount} findings of pull request {ReviewNumber} refer to lines outside the diff; the review summary lists them.")]
    private static partial void LogFindingsOutsideDiff(ILogger logger, int reviewNumber, int findingCount);

    [LoggerMessage(
        EventId = 4787,
        Level = LogLevel.Warning,
        Message =
            "GitHub refused the review of pull request {ReviewNumber} with {CommentCount} inline comments; sending it again with every finding in the summary. Detail: {Detail}")]
    private static partial void LogInlineCommentsRefused(ILogger logger, int reviewNumber, int commentCount, string detail);

    /// <summary>Which findings the summary lists in full, and how many of them lie outside the diff.</summary>
    private sealed class SummaryLayout(IReadOnlySet<ReviewComment> inlineFindings, int outsideDiffCount)
    {
        public bool HasInlineComment(ReviewComment comment) => inlineFindings.Contains(comment);

        public string? OutsideDiffNote => outsideDiffCount switch
        {
            <= 0 => null,
            1 =>
                "1 finding refers to a line outside the diff of this pull request. GitHub accepts review comments only on lines of the diff, so this summary lists the finding in full.",
            _ =>
                $"{outsideDiffCount.ToString(CultureInfo.InvariantCulture)} findings refer to lines outside the diff of this pull request. GitHub accepts review comments only on lines of the diff, so this summary lists these findings in full.",
        };
    }

    private sealed record GitHubComparisonResponse([property: JsonPropertyName("files")] IReadOnlyList<GitHubComparisonFile>? Files);

    private sealed record GitHubComparisonFile(
        [property: JsonPropertyName("filename")]
        string? FileName,
        [property: JsonPropertyName("patch")] string? Patch);

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
        [property: JsonPropertyName("body")] string? Body = null,
        [property: JsonPropertyName("pull_request_review_id")]
        long? ReviewId = null,
        [property: JsonPropertyName("original_line")]
        int? OriginalLine = null);

    internal sealed record GitHubReviewRequest(
        [property: JsonPropertyName("commit_id")]
        string CommitId,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("comments")]
        IReadOnlyList<GitHubInlineReviewComment> Comments)
    {
        /// <summary>The position in the review result of the finding behind each entry of <see cref="Comments" />.</summary>
        [JsonIgnore]
        public IReadOnlyList<int> InlineFindingOrdinals { get; init; } = [];

        /// <summary>Findings with a file and a line that the summary lists because GitHub would not accept their line.</summary>
        [JsonIgnore]
        public int OutsideDiffCount { get; init; }
    }

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
