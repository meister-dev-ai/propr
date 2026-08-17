// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Utilities;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

public sealed class AdoCommentPoster(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IThreadMemoryService? threadMemoryService = null,
    IPostedFindingIndex? postedFindingIndex = null) : IAdoCommentPoster
{
    private const string PostedFindingIndexComponent = "posted_finding_index";
    private const string PostedFindingDuplicateReason = "posted_finding_duplicate";
    private const string PostedFindingNearMissReason = "posted_finding_near_miss";

    /// <summary>How many near misses one posting pass reports. A sample calibrates a threshold; a transcript
    /// of every candidate on a very large pull request is what the diagnostic must not become.</summary>
    private const int MaxRecordedNearMisses = 25;

    /// <summary>Maximum number of characters allowed in a single ADO PR comment to stay safely below API limits.</summary>
    internal const int MaxCommentLength = 30_000;

    private const double FallbackDuplicateSimilarityThreshold = 0.72;
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <summary>
    ///     Creates a single comment thread from an already-resolved body and anchor context, returning the
    ///     provider thread. The seam that lets the posting loop be exercised without a live Azure DevOps connection.
    /// </summary>
    internal delegate Task<GitPullRequestCommentThread> AdoThreadFactory(
        string message,
        CommentThreadContext? threadContext,
        GitPullRequestCommentThreadContext? prThreadContext,
        CancellationToken cancellationToken);

    public async Task<ReviewCommentPostingDiagnosticsDto> PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        Guid? clientId = null,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        AzureDevOpsPublicationContext? publicationContext = null,
        ReviewerIdentity? publicationIdentity = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AdoCommentPoster.Post");
        activity?.SetTag("scm.provider", ScmProvider.AzureDevOps.ToString());
        activity?.SetTag("ado.organization_url", organizationUrl);
        activity?.SetTag("ado.repository_id", repositoryId);
        activity?.SetTag("ado.pull_request_id", pullRequestId);

        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            cancellationToken);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken);
        var botId = connection.AuthorizedIdentity?.Id;
        if (botId.HasValue)
        {
            activity?.SetTag("publication.author.id", botId.Value.ToString("D"));
        }

        var gitClient = await connection.GetClientAsync<GitHttpClient>(cancellationToken);

        // Build a map of normalized file path → changeTrackingId for inline comment anchoring.
        // changeTrackingId is required by ADO to resolve a file thread against the correct diff.
        var changes = await AdoPullRequestIterationChangePager.LoadAllAsync(
            (top, skip, ct) => gitClient.GetPullRequestIterationChangesAsync(
                projectId,
                repositoryId,
                pullRequestId,
                iterationId,
                top,
                skip,
                publicationContext?.CompareToIterationId,
                cancellationToken: ct),
            cancellationToken);

        var changeTrackingIds = BuildChangeTrackingIds(changes);

        return await this.PostResolvedThreadsAsync(
            result,
            (message, threadContext, prThreadContext, token) => CreateThreadAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                message,
                threadContext,
                prThreadContext,
                token),
            botId,
            clientId,
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            publicationContext?.CompareToIterationId,
            changeTrackingIds,
            existingThreads,
            publicationIdentity,
            cancellationToken);
    }

    /// <summary>
    ///     Posts the summary thread and each surviving inline comment through <paramref name="threadFactory" />,
    ///     isolating each creation so one provider rejection cannot abort the rest of the pass. Every failure is
    ///     recorded with its provider error and posting continues; when no thread is posted but at least one was
    ///     rejected, a <see cref="ReviewCommentPublicationFailedException" /> is raised so the pass is not reported
    ///     as a silent success.
    /// </summary>
    internal async Task<ReviewCommentPostingDiagnosticsDto> PostResolvedThreadsAsync(
        ReviewResult result,
        AdoThreadFactory threadFactory,
        Guid? botId,
        Guid? clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId,
        IReadOnlyDictionary<string, int> changeTrackingIds,
        IReadOnlyList<PrCommentThread>? existingThreads,
        ReviewerIdentity? publicationIdentity,
        CancellationToken cancellationToken)
    {
        var diagnostics = new PostingDiagnosticsBuilder(
            result.Comments.Count + result.CarriedForwardCandidatesSkipped,
            result.CarriedForwardCandidatesSkipped,
            ConsideredOpenThreads(existingThreads, botId),
            ConsideredResolvedThreads(existingThreads, botId));

        var state = new PostingState();

        await this.PostSummaryThreadIfNeededAsync(
            result,
            existingThreads,
            botId,
            publicationIdentity,
            threadFactory,
            diagnostics,
            state,
            cancellationToken);

        for (var ordinal = 0; ordinal < result.Comments.Count; ordinal++)
        {
            await this.PostInlineCommentIfNotSuppressedAsync(
                result.Comments[ordinal],
                ordinal,
                existingThreads,
                botId,
                clientId,
                organizationUrl,
                projectId,
                repositoryId,
                pullRequestId,
                iterationId,
                compareToIterationId,
                changeTrackingIds,
                threadFactory,
                diagnostics,
                state,
                cancellationToken);
        }

        var built = diagnostics.Build();

        // Every attempted thread was rejected: surface a publication failure rather than a silent success.
        if (state.PostedThreadCount == 0 && built.FailedCount > 0)
        {
            throw new ReviewCommentPublicationFailedException(built, state.FailureExceptions);
        }

        return built;
    }

    private async Task PostSummaryThreadIfNeededAsync(
        ReviewResult result,
        IReadOnlyList<PrCommentThread>? existingThreads,
        Guid? botId,
        ReviewerIdentity? publicationIdentity,
        AdoThreadFactory threadFactory,
        PostingDiagnosticsBuilder diagnostics,
        PostingState state,
        CancellationToken cancellationToken)
    {
        // Post summary as PR-level thread, skipping if a bot summary already exists.
        if (HasBotSummary(existingThreads, botId, publicationIdentity))
        {
            return;
        }

        try
        {
            var createdSummary = await threadFactory(BuildSummaryText(result), null, null, cancellationToken);
            diagnostics.RecordPostedComments(CaptureCreatedComments(createdSummary, null, null, PostedReviewCommentKind.Summary));
            state.PostedThreadCount++;
        }

        // Isolate any provider failure so it cannot abort the rest of the pass. Request timeouts surface as
        // TaskCanceledException (an OperationCanceledException), so gate on the caller token: only a
        // caller-requested cancellation is allowed to propagate; everything else is recorded and posting continues.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.RecordFailure(new ReviewCommentPostingFailure("summary", null, null, ex.Message));
            state.FailureExceptions.Add(ex);
        }
    }

    private async Task PostInlineCommentIfNotSuppressedAsync(
        ReviewComment comment,
        int ordinal,
        IReadOnlyList<PrCommentThread>? existingThreads,
        Guid? botId,
        Guid? clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId,
        IReadOnlyDictionary<string, int> changeTrackingIds,
        AdoThreadFactory threadFactory,
        PostingDiagnosticsBuilder diagnostics,
        PostingState state,
        CancellationToken cancellationToken)
    {
        var anchorContext = ResolveAnchorContext(
            comment,
            iterationId,
            compareToIterationId,
            changeTrackingIds);
        var (threadContext, prThreadContext) = BuildThreadContexts(anchorContext);
        var normalizedFilePath = anchorContext.NormalizedFilePath;

        var suppression = await this.ResolveInlineCommentSuppressionAsync(
            comment,
            ordinal,
            existingThreads,
            normalizedFilePath,
            botId,
            clientId,
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            diagnostics,
            cancellationToken);
        if (suppression is not null)
        {
            return;
        }

        var posted = await this.PostSingleInlineCommentAsync(
            comment,
            threadContext,
            prThreadContext,
            threadFactory,
            diagnostics,
            state,
            cancellationToken);
        if (posted)
        {
            state.PostedThreadCount++;
        }
    }

    private async Task<string?> ResolveInlineCommentSuppressionAsync(
        ReviewComment comment,
        int ordinal,
        IReadOnlyList<PrCommentThread>? existingThreads,
        string? normalizedFilePath,
        Guid? botId,
        Guid? clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        PostingDiagnosticsBuilder diagnostics,
        CancellationToken cancellationToken)
    {
        var duplicateMatch = FindDeterministicDuplicateMatch(
            existingThreads,
            normalizedFilePath,
            comment.LineNumber,
            comment.Message,
            botId);
        if (duplicateMatch is not null)
        {
            RecordSuppressed(diagnostics, comment, ordinal, duplicateMatch.ReasonCode, duplicateMatch.ThreadId);
            return duplicateMatch.ReasonCode;
        }

        // Cross-increment duplicate protection. It runs ahead of the thread-memory arm because it is the check
        // built for this case: it compares finding text to finding text, with no anchor, no severity and no file
        // in the key, which is what survives the drift observed between increments.
        var postedFindingMatch = await this.FindPostedFindingDuplicateAsync(
            clientId,
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            comment.Message,
            cancellationToken);
        diagnostics.RecordPostedFindingEvaluation(postedFindingMatch, comment, ordinal);

        if (ShouldSuppressAgainstPostedFinding(postedFindingMatch, existingThreads))
        {
            RecordSuppressed(
                diagnostics,
                comment,
                ordinal,
                PostedFindingDuplicateReason,
                postedFindingMatch.ProviderThreadId,
                postedFindingMatch.SimilarityScore);
            return PostedFindingDuplicateReason;
        }

        var historicalMatch = await this.FindHistoricalDuplicateMatchAsync(
            clientId,
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            normalizedFilePath,
            comment.Message,
            cancellationToken);

        diagnostics.RecordHistoricalEvaluation(historicalMatch, ordinal);
        if (historicalMatch.IsDuplicate && historicalMatch.ReasonCode is not null)
        {
            RecordSuppressed(
                diagnostics,
                comment,
                ordinal,
                historicalMatch.ReasonCode,
                historicalMatch.ThreadId,
                historicalMatch.SimilarityScore);
            return historicalMatch.ReasonCode;
        }

        if (!historicalMatch.IsDegraded)
        {
            return null;
        }

        diagnostics.RecordFallbackCheck("deterministic_text_similarity");
        var fallbackMatch = FindFallbackDuplicateMatch(
            existingThreads,
            normalizedFilePath,
            comment.LineNumber,
            comment.Message,
            botId);
        if (fallbackMatch is not null)
        {
            RecordSuppressed(diagnostics, comment, ordinal, fallbackMatch.ReasonCode, fallbackMatch.ThreadId);
            return fallbackMatch.ReasonCode;
        }

        return null;
    }

    /// <summary>
    ///     Counts a suppression and keeps the finding it withheld. A finding kept off the pull request is still a
    ///     finding the review produced, so what was withheld, what it matched and how closely all survive the pass.
    /// </summary>
    private static void RecordSuppressed(
        PostingDiagnosticsBuilder diagnostics,
        ReviewComment comment,
        int ordinal,
        string reasonCode,
        string? matchedThreadId = null,
        float? matchScore = null)
    {
        diagnostics.RecordSuppression(reasonCode);
        diagnostics.RecordSuppressedFinding(
            new ReviewCommentSuppressionRecord(
                ordinal,
                comment.FilePath,
                comment.LineNumber,
                reasonCode,
                matchedThreadId,
                matchScore));
    }

    /// <summary>
    ///     Asks the posted-finding index whether an earlier increment already raised this concern. Never throws:
    ///     an index that cannot answer degrades the check and the finding is posted, because losing a duplicate
    ///     is cheaper than losing a finding.
    /// </summary>
    private async Task<PostedFindingMatchDto> FindPostedFindingDuplicateAsync(
        Guid? clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string message,
        CancellationToken cancellationToken)
    {
        if (postedFindingIndex is null || !clientId.HasValue || string.IsNullOrWhiteSpace(message))
        {
            return PostedFindingMatchDto.NoMatch();
        }

        try
        {
            return await postedFindingIndex.FindDuplicateAsync(
                clientId.Value,
                organizationUrl,
                projectId,
                repositoryId,
                pullRequestId,
                message,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return PostedFindingMatchDto.NoMatch(
                [PostedFindingIndexComponent],
                "Cross-increment duplicate protection ran without the posted-finding index.");
        }
    }

    /// <summary>
    ///     Decides whether a matched earlier finding still justifies withholding this one.
    /// </summary>
    /// <remarks>
    ///     An open thread is the case the index exists for: the concern is already on the pull request waiting for
    ///     an answer. A thread the reviewer closed as won't-fix or by-design is a decision, and re-raising a decided
    ///     concern is exactly what makes reviewers stop trusting the tool.
    ///     <para>
    ///         A thread closed as fixed is the opposite. It says the code moved, so the same concern appearing again
    ///         may be a real recurrence rather than a repeat, and it is posted.
    ///     </para>
    ///     <para>
    ///         A matched thread that is no longer on the pull request is posted too: with the earlier comment gone,
    ///         suppressing would take the concern off the pull request altogether.
    ///     </para>
    /// </remarks>
    private static bool ShouldSuppressAgainstPostedFinding(
        PostedFindingMatchDto match,
        IReadOnlyList<PrCommentThread>? existingThreads)
    {
        if (!match.IsDuplicate || string.IsNullOrWhiteSpace(match.ProviderThreadId))
        {
            return false;
        }

        var matchedThread = (existingThreads ?? [])
            .FirstOrDefault(thread => string.Equals(thread.ThreadId, match.ProviderThreadId, StringComparison.Ordinal));
        if (matchedThread is null)
        {
            return false;
        }

        // A thread whose comments have all been deleted no longer shows the concern to anyone, so treating it
        // as "already raised" would retire the finding while leaving nothing on the pull request to read.
        if (matchedThread.Comments.Count == 0)
        {
            return false;
        }

        // ProPR closing its own thread through auto-resolve leaves exactly the status a reviewer's fix leaves.
        // Only the index row can tell them apart, and it must, or auto-resolve silently disables this check.
        if (match.AutoResolvedByProPr)
        {
            return true;
        }

        return !IsReviewerClaimedFix(matchedThread.Status);
    }

    /// <summary>
    ///     Whether a thread status means a reviewer said the code was changed to address the concern.
    /// </summary>
    /// <remarks>
    ///     Deliberately local to this decision rather than shared with
    ///     <see cref="ThreadResolutionStatusInterpreter" />, which reads both <c>Fixed</c> and <c>Closed</c> as
    ///     claiming a fix. That reading is right where it is used, gating what becomes suppression memory,
    ///     because a thread closed before the code changed must not teach a later review to drop a still-valid
    ///     finding. The question here is the opposite one, whether the concern was already put to the reviewer,
    ///     and only <c>Fixed</c> asserts a change. Azure DevOps <c>Closed</c> is how a thread is dismissed, and
    ///     reading that as a fix is what lets a dismissed concern come back every increment.
    ///     <para>
    ///         Residual risk, stated plainly: a reviewer who closes rather than marks fixed after a real repair
    ///         will have a genuine recurrence withheld. Nothing at this point in the pipeline knows whether the
    ///         anchored code moved, so the ambiguity cannot be resolved here.
    ///     </para>
    /// </remarks>
    private static bool IsReviewerClaimedFix(string? status)
    {
        return string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> PostSingleInlineCommentAsync(
        ReviewComment comment,
        CommentThreadContext? threadContext,
        GitPullRequestCommentThreadContext? prThreadContext,
        AdoThreadFactory threadFactory,
        PostingDiagnosticsBuilder diagnostics,
        PostingState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdThread = await threadFactory(
                FormatInlineCommentBody(comment),
                threadContext,
                prThreadContext,
                cancellationToken);

            diagnostics.RecordPosted();
            diagnostics.RecordPostedComments(CaptureCreatedComments(createdThread, comment.FilePath, comment.LineNumber));
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.RecordFailure(new ReviewCommentPostingFailure("inline", comment.FilePath, comment.LineNumber, ex.Message));
            state.FailureExceptions.Add(ex);
            return false;
        }
    }

    private sealed class PostingState
    {
        public int PostedThreadCount;
        public List<Exception> FailureExceptions { get; } = [];
    }

    /// <summary>
    ///     Builds the summary comment text from a <see cref="ReviewResult" />.
    ///     When the result includes carried-forward file paths, a section listing those files
    ///     is appended to the summary. All content is HTML-sanitized to prevent injection.
    /// </summary>
    internal static string BuildSummaryText(ReviewResult result)
    {
        var sb = new StringBuilder(HarvestedThreadEligibility.SummaryPrefix + "\n\n");
        sb.Append(HtmlSanitizer.RenderForDisplay(result.Summary, ReviewBodyRenderingMode.Summary).RenderedText);

        if (result.CarriedForwardFilePaths.Count > 0)
        {
            sb.Append($"\n\n**Carried forward unchanged files** ({result.CarriedForwardFilePaths.Count} files — results from prior review retained)\n\n");
            foreach (var path in result.CarriedForwardFilePaths)
            {
                var renderedPath = HtmlSanitizer.RenderForDisplay(path, ReviewBodyRenderingMode.Summary).RenderedText;
                sb.Append($"- {renderedPath}\n");
            }
        }

        if (result.BudgetSoftCapped)
        {
            sb.Append($"\n\n{ContextBudgetSummarySections.FormatBudgetSoftCapNote(result)}\n\n");
            foreach (var path in result.BudgetSoftCapSkippedFilePaths)
            {
                var renderedPath = HtmlSanitizer.RenderForDisplay(path, ReviewBodyRenderingMode.Summary).RenderedText;
                sb.Append($"- {renderedPath}\n");
            }
        }

        if (result.ContextDegradedFilePaths.Count > 0)
        {
            sb.Append(
                $"\n\n**Reviewed diff-only** ({result.ContextDegradedFilePaths.Count} files — too large for full context, reviewed from the diff alone)\n\n");
            foreach (var path in result.ContextDegradedFilePaths)
            {
                var renderedPath = HtmlSanitizer.RenderForDisplay(path, ReviewBodyRenderingMode.Summary).RenderedText;
                sb.Append($"- {renderedPath}\n");
            }
        }

        if (result.ContextSkippedFilePaths.Count > 0)
        {
            sb.Append($"\n\n**Skipped — exceeds model context window** ({result.ContextSkippedFilePaths.Count} files — not reviewed)\n\n");
            foreach (var path in result.ContextSkippedFilePaths)
            {
                var renderedPath = HtmlSanitizer.RenderForDisplay(path, ReviewBodyRenderingMode.Summary).RenderedText;
                sb.Append($"- {renderedPath}\n");
            }
        }

        return sb.ToString();
    }

    internal static string FormatInlineCommentBody(ReviewComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        var severityPrefix = comment.Severity switch
        {
            CommentSeverity.Error => "ERROR",
            CommentSeverity.Warning => "WARNING",
            CommentSeverity.Suggestion => "SUGGESTION",
            _ => "INFO",
        };
        var renderedMessage = HtmlSanitizer.RenderForDisplay(comment.Message, ReviewBodyRenderingMode.InlineComment);
        return $"{severityPrefix}: {renderedMessage.RenderedText}";
    }

    /// <summary>
    ///     Returns <c>true</c> if a bot-authored PR-level summary thread already exists.
    ///     Bot authorship is determined by comparing the comment's <see cref="PrThreadComment.AuthorId" />
    ///     against the current connection's authorized identity (<paramref name="botId" />).
    /// </summary>
    internal static bool HasBotSummary(
        IReadOnlyList<PrCommentThread>? threads,
        Guid? botId,
        ReviewerIdentity? publicationIdentity = null)
    {
        return (threads ?? []).Any(t =>
            t.FilePath is null &&
            t.Comments.Any(c => IsBotAuthor(c.AuthorId, botId, c.AuthorName, publicationIdentity)
                                && c.Content.StartsWith(HarvestedThreadEligibility.SummaryPrefix, StringComparison.Ordinal)));
    }

    /// <summary>
    ///     Returns <c>true</c> if a bot-authored thread already exists at the given file path and line number.
    ///     Bot authorship is determined by comparing the comment's <see cref="PrThreadComment.AuthorId" />
    ///     against the current connection's authorized identity (<paramref name="botId" />).
    /// </summary>
    internal static bool HasBotThreadAt(
        IReadOnlyList<PrCommentThread>? threads,
        string? filePath,
        int? lineNumber,
        Guid? botId)
    {
        if (filePath is null)
        {
            return false;
        }

        return FindLocationDuplicateMatch(threads, filePath, lineNumber, botId) is not null;
    }

    /// <summary>
    ///     Returns <c>true</c> if the comment was authored by the bot, identified by VSS identity GUID equality.
    ///     Returns <c>false</c> if either GUID is unknown.
    /// </summary>
    internal static bool IsBotAuthor(
        Guid? authorId,
        Guid? botId,
        string? authorName = null,
        ReviewerIdentity? publicationIdentity = null)
    {
        if (authorId.HasValue && botId.HasValue && authorId.Value == botId.Value)
        {
            return true;
        }

        if (publicationIdentity is null || string.IsNullOrWhiteSpace(authorName))
        {
            return false;
        }

        return string.Equals(authorName, publicationIdentity.DisplayName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(authorName, publicationIdentity.Login, StringComparison.OrdinalIgnoreCase);
    }

    internal static DuplicateSuppressionMatch? FindDeterministicDuplicateMatch(
        IReadOnlyList<PrCommentThread>? threads,
        string? filePath,
        int? lineNumber,
        string message,
        Guid? botId)
    {
        var locationMatch = FindLocationDuplicateMatch(threads, filePath, lineNumber, botId);
        if (locationMatch is not null)
        {
            return locationMatch;
        }

        var normalizedMessage = NormalizeCommentMessage(message);
        if (normalizedMessage.Length == 0)
        {
            return null;
        }

        foreach (var thread in GetBotThreadsWithCompatibleContext(threads, filePath, lineNumber, botId))
        {
            if (thread.Comments.Any(comment =>
                    IsBotAuthor(comment.AuthorId, botId) &&
                    NormalizeCommentMessage(comment.Content) == normalizedMessage))
            {
                return new DuplicateSuppressionMatch("normalized_text_match", thread.ThreadId);
            }
        }

        return null;
    }

    internal static DuplicateSuppressionMatch? FindFallbackDuplicateMatch(
        IReadOnlyList<PrCommentThread>? threads,
        string? filePath,
        int? lineNumber,
        string message,
        Guid? botId)
    {
        var normalizedMessage = NormalizeCommentMessage(message);
        if (normalizedMessage.Length == 0)
        {
            return null;
        }

        var bestMatch = GetBotThreadsWithCompatibleContext(threads, filePath, lineNumber, botId)
            .Select(thread => new
            {
                thread.ThreadId,
                Score = thread.Comments
                    .Where(comment => IsBotAuthor(comment.AuthorId, botId))
                    .Select(comment => CalculateTextSimilarity(
                        normalizedMessage,
                        NormalizeCommentMessage(comment.Content)))
                    .DefaultIfEmpty(0d)
                    .Max(),
            })
            .Where(candidate => candidate.Score >= FallbackDuplicateSimilarityThreshold)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();

        return bestMatch is null
            ? null
            : new DuplicateSuppressionMatch("fallback_duplicate_match", bestMatch.ThreadId);
    }

    internal static PublicationAnchorContext ResolveAnchorContext(
        ReviewComment comment,
        int iterationId,
        int? compareToIterationId,
        IReadOnlyDictionary<string, int> changeTrackingIds)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(changeTrackingIds);

        var normalizedFilePath = NormalizeOptionalPath(comment.FilePath);
        var resolvedLineNumber = NormalizeLineNumber(comment.LineNumber);
        var compareReference = BuildCompareRevisionReference(compareToIterationId, iterationId);

        if (normalizedFilePath is null)
        {
            return new PublicationAnchorContext(
                comment.FilePath,
                comment.LineNumber,
                null,
                null,
                PublicationAnchorPrecision.PrLevel,
                CompareRevisionReference: compareReference);
        }

        if (resolvedLineNumber.HasValue && changeTrackingIds.TryGetValue(normalizedFilePath, out var trackingId))
        {
            return new PublicationAnchorContext(
                comment.FilePath,
                comment.LineNumber,
                normalizedFilePath,
                resolvedLineNumber,
                PublicationAnchorPrecision.Inline,
                trackingId.ToString(),
                compareReference);
        }

        return new PublicationAnchorContext(
            comment.FilePath,
            comment.LineNumber,
            normalizedFilePath,
            null,
            PublicationAnchorPrecision.File,
            CompareRevisionReference: compareReference);
    }

    internal static (CommentThreadContext? ThreadContext, GitPullRequestCommentThreadContext? PrThreadContext)
        BuildThreadContexts(PublicationAnchorContext anchorContext)
    {
        ArgumentNullException.ThrowIfNull(anchorContext);

        return anchorContext.AnchorPrecision switch
        {
            PublicationAnchorPrecision.Inline => BuildInlineThreadContexts(anchorContext),
            PublicationAnchorPrecision.File => BuildFileThreadContexts(anchorContext),
            _ => (null, null),
        };
    }

    private static async Task<GitPullRequestCommentThread> CreateThreadAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string message,
        CommentThreadContext? threadContext,
        GitPullRequestCommentThreadContext? prThreadContext,
        CancellationToken ct)
    {
        var content = TruncateIfNeeded(message);
        var thread = new GitPullRequestCommentThread
        {
            Comments = [new Comment { Content = content, CommentType = CommentType.Text }],
            Status = CommentThreadStatus.Active,
            ThreadContext = threadContext,
            PullRequestThreadContext = prThreadContext,
        };
        return await gitClient.CreateThreadAsync(
            thread,
            repositoryId,
            pullRequestId,
            projectId,
            ct);
    }

    // Best-effort provenance capture: maps each created comment's id (the value the thread crawler later
    // reports as the comment id) and its owning thread id from the response Azure DevOps returns. A null
    // or empty response yields no refs and never disrupts publishing.
    internal static IReadOnlyList<PostedReviewCommentRef> CaptureCreatedComments(
        GitPullRequestCommentThread? createdThread,
        string? filePath,
        int? line,
        PostedReviewCommentKind threadKind = PostedReviewCommentKind.Inline)
    {
        if (createdThread?.Comments is not { Count: > 0 } comments)
        {
            return [];
        }

        var threadId = createdThread.Id.ToString(CultureInfo.InvariantCulture);
        return comments
            .Where(comment => comment.Id > 0)
            .Select(comment => new PostedReviewCommentRef(
                comment.Id.ToString(CultureInfo.InvariantCulture),
                threadId,
                filePath,
                line,
                threadKind))
            .ToList();
    }

    internal static string TruncateIfNeeded(string message)
    {
        if (message.Length <= MaxCommentLength)
        {
            return message;
        }

        const string notice = "\n\n> *(Review comment truncated — view the full review in the MeisterProPR admin UI)*";
        var cutoff = MaxCommentLength - notice.Length;

        // Trim to last whitespace boundary so we don't cut mid-word.
        var boundary = message.LastIndexOf(' ', cutoff);
        if (boundary < 1)
        {
            boundary = cutoff;
        }

        return message[..boundary] + notice;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    /// <summary>
    ///     Builds a map of normalized file path → changeTrackingId for inline comment anchoring.
    ///     ADO can return multiple change entries for the same path within a single iteration
    ///     (e.g. force-pushed commits, rename + edit combinations, or overlapping pages), so the
    ///     map is collapsed to one entry per path. A change that still has content in the iteration
    ///     (not a pure delete) is preferred so inline comments anchor to the correct side of the diff.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> BuildChangeTrackingIds(IEnumerable<GitPullRequestChange> changes)
    {
        return changes
            .Where(c => c.Item?.Path is not null)
            .GroupBy(c => NormalizePath(c.Item!.Path!), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(c => c.ChangeType.HasFlag(VersionControlChangeType.Delete) ? 1 : 0)
                    .First()
                    .ChangeTrackingId,
                StringComparer.Ordinal);
    }

    private static (CommentThreadContext? ThreadContext, GitPullRequestCommentThreadContext? PrThreadContext)
        BuildInlineThreadContexts(PublicationAnchorContext anchorContext)
    {
        if (anchorContext.NormalizedFilePath is null ||
            !anchorContext.ResolvedLineNumber.HasValue ||
            !int.TryParse(anchorContext.ProviderTrackingReference, out var trackingId))
        {
            return BuildFileThreadContexts(anchorContext with { ResolvedLineNumber = null, AnchorPrecision = PublicationAnchorPrecision.File });
        }

        var threadContext = new CommentThreadContext
        {
            FilePath = anchorContext.NormalizedFilePath,
            RightFileStart = new CommentPosition { Line = anchorContext.ResolvedLineNumber.Value, Offset = 1 },
            RightFileEnd = new CommentPosition { Line = anchorContext.ResolvedLineNumber.Value, Offset = 1 },
        };

        var prThreadContext = new GitPullRequestCommentThreadContext
        {
            ChangeTrackingId = trackingId,
            IterationContext = BuildIterationContext(anchorContext.CompareRevisionReference),
        };

        return (threadContext, prThreadContext);
    }

    private static (CommentThreadContext? ThreadContext, GitPullRequestCommentThreadContext? PrThreadContext)
        BuildFileThreadContexts(PublicationAnchorContext anchorContext)
    {
        if (anchorContext.NormalizedFilePath is null)
        {
            return (null, null);
        }

        return (new CommentThreadContext
        {
            FilePath = anchorContext.NormalizedFilePath,
            RightFileStart = null,
            RightFileEnd = null,
        }, null);
    }

    private static CommentIterationContext? BuildIterationContext(string? compareRevisionReference)
    {
        var (firstComparingIteration, secondComparingIteration) = ParseCompareRevisionReference(compareRevisionReference);

        // The iteration-context fields are shorts; a pair that cannot be represented must fall
        // back to an unpinned thread rather than wrap negative and get the payload rejected.
        if (firstComparingIteration is <= 0 or > short.MaxValue || secondComparingIteration is <= 0 or > short.MaxValue)
        {
            return null;
        }

        return new CommentIterationContext
        {
            FirstComparingIteration = (short)firstComparingIteration,
            SecondComparingIteration = (short)secondComparingIteration,
        };
    }

    // Builds the "first:second" comparing-iteration pair the inline thread is pinned to. The
    // review computes right-side line numbers against the reviewed iteration's source commit, so
    // the posted thread must carry that iteration as its second comparing iteration; a thread
    // created without an iteration context is resolved by Azure DevOps against the latest
    // iteration at posting time, which shifts every anchor when the pull request advanced
    // mid-review. A full (non-incremental) review pins the full-diff view (iteration 1 → N),
    // matching what the Azure DevOps web UI sends for comments on the all-updates diff.
    private static string? BuildCompareRevisionReference(int? compareToIterationId, int iterationId)
    {
        if (iterationId <= 0)
        {
            return null;
        }

        var firstComparingIteration = compareToIterationId is > 0 ? compareToIterationId.Value : 1;
        return $"{firstComparingIteration}:{iterationId}";
    }

    private static (int FirstComparingIteration, int SecondComparingIteration) ParseCompareRevisionReference(string? compareRevisionReference)
    {
        if (string.IsNullOrWhiteSpace(compareRevisionReference))
        {
            return (0, 0);
        }

        var parts = compareRevisionReference.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return (0, 0);
        }

        return (
            int.TryParse(parts[0], out var firstComparingIteration) ? firstComparingIteration : 0,
            int.TryParse(parts[1], out var secondComparingIteration) ? secondComparingIteration : 0);
    }

    private async Task<HistoricalDuplicateSuppressionMatchDto> FindHistoricalDuplicateMatchAsync(
        Guid? clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string? filePath,
        string message,
        CancellationToken cancellationToken)
    {
        if (!clientId.HasValue)
        {
            return HistoricalDuplicateSuppressionMatchDto.NoMatch(
                ["thread_memory_client_context"],
                "Historical duplicate protection ran without a client-scoped thread-memory context.");
        }

        if (threadMemoryService is null)
        {
            return HistoricalDuplicateSuppressionMatchDto.NoMatch(
                ["thread_memory_service"],
                "Historical duplicate protection ran without the thread-memory service.");
        }

        try
        {
            return await threadMemoryService.FindDuplicateSuppressionMatchAsync(
                clientId.Value,
                organizationUrl,
                projectId,
                repositoryId,
                pullRequestId,
                filePath,
                message,
                cancellationToken);
        }
        catch
        {
            return HistoricalDuplicateSuppressionMatchDto.NoMatch(
                ["thread_memory_service"],
                "Historical duplicate protection ran without the thread-memory service.");
        }
    }

    private static DuplicateSuppressionMatch? FindLocationDuplicateMatch(
        IReadOnlyList<PrCommentThread>? threads,
        string? filePath,
        int? lineNumber,
        Guid? botId)
    {
        var normalizedFilePath = NormalizeOptionalPath(filePath);

        // A location match needs a location. A candidate without a file anchor would otherwise match the
        // pull-request-level summary thread, because two absent paths and two absent line numbers compare
        // equal, and every fileless finding would be discarded from the second review increment onward.
        // Those candidates are compared on their content by the text tiers instead.
        if (normalizedFilePath is null)
        {
            return null;
        }

        var normalizedLine = NormalizeLineNumber(lineNumber);

        foreach (var thread in threads ?? [])
        {
            if (!thread.Comments.Any(comment => IsBotAuthor(comment.AuthorId, botId)))
            {
                continue;
            }

            if (!AreEquivalentAnchors(normalizedFilePath, normalizedLine, thread.FilePath, thread.LineNumber))
            {
                continue;
            }

            var reason = IsResolvedStatus(thread.Status)
                ? "resolved_thread_match"
                : "normalized_location_match";
            return new DuplicateSuppressionMatch(reason, thread.ThreadId);
        }

        return null;
    }

    private static IEnumerable<PrCommentThread> GetBotThreadsWithCompatibleContext(
        IReadOnlyList<PrCommentThread>? threads,
        string? filePath,
        int? lineNumber,
        Guid? botId)
    {
        var normalizedFilePath = NormalizeOptionalPath(filePath);
        var normalizedLine = NormalizeLineNumber(lineNumber);

        return (threads ?? [])
            .Where(thread =>
                thread.Comments.Any(comment => IsBotAuthor(comment.AuthorId, botId)) &&
                HasCompatibleTextContext(normalizedFilePath, normalizedLine, thread.FilePath, thread.LineNumber));
    }

    private static bool ConsideredOpenThreads(IReadOnlyList<PrCommentThread>? threads, Guid? botId)
    {
        return (threads ?? []).Any(thread =>
            thread.Comments.Any(comment => IsBotAuthor(comment.AuthorId, botId)) &&
            !IsResolvedStatus(thread.Status));
    }

    private static bool ConsideredResolvedThreads(IReadOnlyList<PrCommentThread>? threads, Guid? botId)
    {
        return (threads ?? []).Any(thread =>
            thread.Comments.Any(comment => IsBotAuthor(comment.AuthorId, botId)) &&
            IsResolvedStatus(thread.Status));
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);
    }

    private static int? NormalizeLineNumber(int? lineNumber)
    {
        return lineNumber is > 0 ? lineNumber : null;
    }

    /// <summary>
    ///     Compares two anchors for equivalence after normalizing the thread side.
    ///     <paramref name="filePath" /> must already be normalized and must be present: two absent paths
    ///     compare equal here, which is why callers refuse an anchor match for a candidate that carries no
    ///     file path rather than relying on this predicate to reject it.
    /// </summary>
    private static bool AreEquivalentAnchors(
        string? filePath,
        int? lineNumber,
        string? otherFilePath,
        int? otherLineNumber)
    {
        var normalizedOtherPath = NormalizeOptionalPath(otherFilePath);
        if (!string.Equals(filePath, normalizedOtherPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NormalizeLineNumber(lineNumber) == NormalizeLineNumber(otherLineNumber);
    }

    private static bool HasCompatibleTextContext(
        string? filePath,
        int? lineNumber,
        string? otherFilePath,
        int? otherLineNumber)
    {
        var normalizedOtherPath = NormalizeOptionalPath(otherFilePath);
        if (!string.Equals(filePath, normalizedOtherPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedLine = NormalizeLineNumber(lineNumber);
        var normalizedOtherLine = NormalizeLineNumber(otherLineNumber);
        if (!normalizedLine.HasValue || !normalizedOtherLine.HasValue)
        {
            return true;
        }

        return Math.Abs(normalizedLine.Value - normalizedOtherLine.Value) <= 1;
    }

    private static bool IsResolvedStatus(string? status)
    {
        return status is not null && status.Trim().ToLowerInvariant() switch
        {
            "fixed" => true,
            "closed" => true,
            "wontfix" => true,
            "bydesign" => true,
            _ => false,
        };
    }

    private static string NormalizeCommentMessage(string message)
    {
        var sanitized = HtmlSanitizer.Sanitize(message).Trim();

        // These four prefixes stay English whatever output language the client configured. They are the severity
        // labels this poster itself prepends, and stripping them is how a re-review recognizes a comment it already
        // posted. Translating them would make every previously posted comment stop matching, so duplicate
        // suppression would silently re-post the same finding.
        foreach (var prefix in new[] { "ERROR:", "WARNING:", "SUGGESTION:", "INFO:" })
        {
            if (sanitized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                sanitized = sanitized[prefix.Length..].TrimStart();
                break;
            }
        }

        var buffer = new StringBuilder(sanitized.Length);
        var wroteWhitespace = false;
        foreach (var character in sanitized)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer.Append(char.ToLowerInvariant(character));
                wroteWhitespace = false;
                continue;
            }

            if (char.IsWhiteSpace(character) && !wroteWhitespace && buffer.Length > 0)
            {
                buffer.Append(' ');
                wroteWhitespace = true;
            }
        }

        return buffer.ToString().Trim();
    }

    private static double CalculateTextSimilarity(string first, string second)
    {
        var firstTokens = Tokenize(first);
        var secondTokens = Tokenize(second);
        if (firstTokens.Count == 0 || secondTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = firstTokens.Intersect(secondTokens).Count();
        var union = firstTokens.Union(secondTokens).Count();
        return union == 0 ? 0d : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var isWordChar = index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '\'');
            if (isWordChar)
            {
                continue;
            }

            if (index > start)
            {
                var token = text[start..index].ToLowerInvariant();
                if (token.Length >= 3)
                {
                    tokens.Add(token);
                }
            }

            start = index + 1;
        }

        return tokens;
    }

    internal sealed record DuplicateSuppressionMatch(string ReasonCode, string? ThreadId);

    private sealed class PostingDiagnosticsBuilder
    {
        private readonly HashSet<string> _degradedComponents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fallbackChecks = new(StringComparer.Ordinal);
        private readonly List<ReviewCommentPostingFailure> _postingFailures = [];
        private readonly List<PostedReviewCommentRef> _postedComments = [];
        private readonly List<ReviewCommentSuppressionRecord> _suppressedFindings = [];
        private readonly List<ReviewCommentSuppressionRecord> _postedFindingNearMisses = [];
        private readonly HashSet<int> _affectedCandidateOrdinals = [];
        private readonly Dictionary<string, int> _suppressionReasons = new(StringComparer.Ordinal);
        private readonly bool _consideredOpenThreads;
        private readonly bool _consideredResolvedThreads;
        private int _affectedCandidateCount;
        private string? _degradedCause;
        private int _postedCount;
        private int _suppressedCount;

        public PostingDiagnosticsBuilder(
            int candidateCount,
            int carriedForwardCandidatesSkipped,
            bool consideredOpenThreads,
            bool consideredResolvedThreads)
        {
            this.CandidateCount = candidateCount;
            this.CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped;
            this._consideredOpenThreads = consideredOpenThreads;
            this._consideredResolvedThreads = consideredResolvedThreads;
            if (carriedForwardCandidatesSkipped > 0)
            {
                this.RecordSuppression("carried_forward_source", carriedForwardCandidatesSkipped);
            }
        }

        public int CandidateCount { get; }

        public int CarriedForwardCandidatesSkipped { get; }

        public void RecordPosted()
        {
            this._postedCount++;
        }

        public void RecordPostedComments(IReadOnlyList<PostedReviewCommentRef> comments)
        {
            this._postedComments.AddRange(comments);
        }

        public void RecordSuppression(string reasonCode, int count = 1)
        {
            this._suppressedCount += count;
            this._suppressionReasons[reasonCode] = this._suppressionReasons.GetValueOrDefault(reasonCode) + count;
        }

        public void RecordHistoricalEvaluation(HistoricalDuplicateSuppressionMatchDto match, int ordinal)
        {
            foreach (var component in match.DegradedComponents)
            {
                this._degradedComponents.Add(component);
            }

            foreach (var fallbackCheck in match.FallbackChecks)
            {
                this._fallbackChecks.Add(fallbackCheck);
            }

            if (match.IsDegraded)
            {
                this.RecordAffectedCandidate(ordinal);
                this._degradedCause ??= match.DegradedCause;
            }
        }

        public void RecordFallbackCheck(string fallbackCheck)
        {
            this._fallbackChecks.Add(fallbackCheck);
        }

        /// <summary>
        ///     Records the degraded state of a posted-finding index lookup without treating it as a suppression.
        ///     A lookup that could not run means cross-increment protection did not happen for this candidate,
        ///     which has to reach the job telemetry rather than pass as a clean miss.
        /// </summary>
        public void RecordPostedFindingEvaluation(
            PostedFindingMatchDto match,
            ReviewComment comment,
            int ordinal)
        {
            // A near miss is recorded whether or not the lookup degraded, and capped: this runs once per
            // candidate, and a very large pull request must not turn a diagnostic into a flood.
            if (match is { NearMissScore: not null, NearMissProviderThreadId: not null }
                && this._postedFindingNearMisses.Count < MaxRecordedNearMisses)
            {
                this._postedFindingNearMisses.Add(
                    new ReviewCommentSuppressionRecord(
                        ordinal,
                        comment.FilePath,
                        comment.LineNumber,
                        PostedFindingNearMissReason,
                        match.NearMissProviderThreadId,
                        match.NearMissScore));
            }

            foreach (var component in match.DegradedComponents)
            {
                this._degradedComponents.Add(component);
            }

            if (!match.IsDegraded)
            {
                return;
            }

            this.RecordAffectedCandidate(ordinal);
            this._degradedCause ??= match.DegradedCause;
        }

        /// <summary>
        ///     Counts a candidate whose duplicate protection ran degraded, at most once. Several tiers can
        ///     report the same candidate, and counting each report would put the affected count above the
        ///     number of candidates the review produced.
        /// </summary>
        public void RecordAffectedCandidate(int ordinal)
        {
            if (this._affectedCandidateOrdinals.Add(ordinal))
            {
                this._affectedCandidateCount++;
            }
        }

        public void RecordSuppressedFinding(ReviewCommentSuppressionRecord record)
        {
            this._suppressedFindings.Add(record);
        }

        public void RecordFailure(ReviewCommentPostingFailure failure)
        {
            this._postingFailures.Add(failure);
        }

        public ReviewCommentPostingDiagnosticsDto Build()
        {
            return new ReviewCommentPostingDiagnosticsDto
            {
                CandidateCount = this.CandidateCount,
                PostedCount = this._postedCount,
                SuppressedCount = this._suppressedCount,
                FailedCount = this._postingFailures.Count,
                CarriedForwardCandidatesSkipped = this.CarriedForwardCandidatesSkipped,
                SuppressionReasons = new Dictionary<string, int>(this._suppressionReasons, StringComparer.Ordinal),
                ConsideredOpenThreads = this._consideredOpenThreads,
                ConsideredResolvedThreads = this._consideredResolvedThreads,
                FallbackChecks = this._fallbackChecks.OrderBy(value => value, StringComparer.Ordinal)
                    .ToList()
                    .AsReadOnly(),
                DegradedComponents = this._degradedComponents.OrderBy(value => value, StringComparer.Ordinal)
                    .ToList()
                    .AsReadOnly(),
                DegradedCause = this._degradedCause,
                AffectedCandidateCount = this._affectedCandidateCount,
                PostedComments = this._postedComments.AsReadOnly(),
                PostingFailures = this._postingFailures.AsReadOnly(),
                SuppressedFindings = this._suppressedFindings.AsReadOnly(),
                PostedFindingNearMisses = this._postedFindingNearMisses
                    .OrderByDescending(record => record.MatchScore ?? 0f)
                    .ToList()
                    .AsReadOnly(),
            };
        }
    }
}
