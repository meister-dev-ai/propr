// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Utilities;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

public sealed class AdoCommentPoster(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IThreadMemoryService? threadMemoryService = null) : IAdoCommentPoster
{
    /// <summary>Maximum number of characters allowed in a single ADO PR comment to stay safely below API limits.</summary>
    internal const int MaxCommentLength = 30_000;

    private const double FallbackDuplicateSimilarityThreshold = 0.72;
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

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

        var diagnostics = new PostingDiagnosticsBuilder(
            result.Comments.Count + result.CarriedForwardCandidatesSkipped,
            result.CarriedForwardCandidatesSkipped,
            ConsideredOpenThreads(existingThreads, null),
            ConsideredResolvedThreads(existingThreads, null));

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

        diagnostics.SetThreadCoverage(
            ConsideredOpenThreads(existingThreads, botId),
            ConsideredResolvedThreads(existingThreads, botId));
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

        // Post summary as PR-level thread, skipping if a bot summary already exists.
        if (!HasBotSummary(existingThreads, botId, publicationIdentity))
        {
            var createdSummary = await CreateThreadAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                BuildSummaryText(result),
                null,
                null,
                cancellationToken);
            diagnostics.RecordPostedComments(CaptureCreatedComments(createdSummary, null, null));
        }

        // Post each inline comment, skipping locations the bot has already covered.
        foreach (var comment in result.Comments)
        {
            var anchorContext = ResolveAnchorContext(
                comment,
                iterationId,
                publicationContext?.CompareToIterationId,
                changeTrackingIds);
            var (threadContext, prThreadContext) = BuildThreadContexts(anchorContext);
            var normalizedFilePath = anchorContext.NormalizedFilePath;

            var duplicateMatch = FindDeterministicDuplicateMatch(
                existingThreads,
                normalizedFilePath,
                comment.LineNumber,
                comment.Message,
                botId);

            if (duplicateMatch is not null)
            {
                diagnostics.RecordSuppression(duplicateMatch.ReasonCode);
                continue;
            }

            var historicalMatch = await this.FindHistoricalDuplicateMatchAsync(
                clientId,
                repositoryId,
                pullRequestId,
                normalizedFilePath,
                comment.Message,
                cancellationToken);

            diagnostics.RecordHistoricalEvaluation(historicalMatch);
            if (historicalMatch.IsDuplicate && historicalMatch.ReasonCode is not null)
            {
                diagnostics.RecordSuppression(historicalMatch.ReasonCode);
                continue;
            }

            if (historicalMatch.IsDegraded)
            {
                diagnostics.RecordFallbackCheck("deterministic_text_similarity");
                var fallbackMatch = FindFallbackDuplicateMatch(
                    existingThreads,
                    normalizedFilePath,
                    comment.LineNumber,
                    comment.Message,
                    botId);

                if (fallbackMatch is not null)
                {
                    diagnostics.RecordSuppression(fallbackMatch.ReasonCode);
                    continue;
                }
            }

            var createdThread = await CreateThreadAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                FormatInlineCommentBody(comment),
                threadContext,
                prThreadContext,
                cancellationToken);

            diagnostics.RecordPosted();
            diagnostics.RecordPostedComments(CaptureCreatedComments(createdThread, comment.FilePath, comment.LineNumber));
        }

        return diagnostics.Build();
    }

    /// <summary>
    ///     Builds the summary comment text from a <see cref="ReviewResult" />.
    ///     When the result includes carried-forward file paths, a section listing those files
    ///     is appended to the summary. All content is HTML-sanitized to prevent injection.
    /// </summary>
    internal static string BuildSummaryText(ReviewResult result)
    {
        var sb = new StringBuilder("**AI Review Summary**\n\n");
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
                                && c.Content.StartsWith("**AI Review Summary**", StringComparison.Ordinal)));
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
        int? line)
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
                line))
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

    internal sealed record DuplicateSuppressionMatch(string ReasonCode, long ThreadId);

    private sealed class PostingDiagnosticsBuilder
    {
        private readonly HashSet<string> _degradedComponents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fallbackChecks = new(StringComparer.Ordinal);
        private readonly List<PostedReviewCommentRef> _postedComments = [];
        private readonly Dictionary<string, int> _suppressionReasons = new(StringComparer.Ordinal);
        private int _affectedCandidateCount;
        private bool _consideredOpenThreads;
        private bool _consideredResolvedThreads;
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

        public void SetThreadCoverage(bool consideredOpenThreads, bool consideredResolvedThreads)
        {
            this._consideredOpenThreads = consideredOpenThreads;
            this._consideredResolvedThreads = consideredResolvedThreads;
        }

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

        public void RecordHistoricalEvaluation(HistoricalDuplicateSuppressionMatchDto match)
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
                this._affectedCandidateCount++;
                this._degradedCause ??= match.DegradedCause;
            }
        }

        public void RecordFallbackCheck(string fallbackCheck)
        {
            this._fallbackChecks.Add(fallbackCheck);
        }

        public ReviewCommentPostingDiagnosticsDto Build()
        {
            return new ReviewCommentPostingDiagnosticsDto
            {
                CandidateCount = this.CandidateCount,
                PostedCount = this._postedCount,
                SuppressedCount = this._suppressedCount,
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
            };
        }
    }
}
