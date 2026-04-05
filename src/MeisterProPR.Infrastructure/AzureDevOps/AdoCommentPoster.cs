// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Utilities;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed class AdoCommentPoster(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    IThreadMemoryService? threadMemoryService = null) : IAdoCommentPoster
{
    /// <summary>Maximum number of characters allowed in a single ADO PR comment to stay safely below API limits.</summary>
    internal const int MaxCommentLength = 30_000;
    private const double FallbackDuplicateSimilarityThreshold = 0.72;

    public async Task<ReviewCommentPostingDiagnosticsDto> PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        Guid? clientId = null,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new PostingDiagnosticsBuilder(
            result.Comments.Count + result.CarriedForwardCandidatesSkipped,
            result.CarriedForwardCandidatesSkipped,
            ConsideredOpenThreads(existingThreads, botId: null),
            ConsideredResolvedThreads(existingThreads, botId: null));

        var credentials = clientId.HasValue
            ? await credentialRepository.GetByClientIdAsync(clientId.Value, cancellationToken)
            : null;
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken: cancellationToken);
        var botId = connection.AuthorizedIdentity?.Id;
        diagnostics.SetThreadCoverage(
            ConsideredOpenThreads(existingThreads, botId),
            ConsideredResolvedThreads(existingThreads, botId));
        var gitClient = connection.GetClient<GitHttpClient>();

        // Build a map of normalized file path → changeTrackingId for inline comment anchoring.
        // changeTrackingId is required by ADO to resolve a file thread against the correct diff.
        var changes = await gitClient.GetPullRequestIterationChangesAsync(
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            cancellationToken: cancellationToken);

        var changeTrackingIds = (changes.ChangeEntries ?? [])
            .Where(c => c.Item?.Path is not null)
            .ToDictionary(
                c => NormalizePath(c.Item!.Path!),
                c => c.ChangeTrackingId);

        // Post summary as PR-level thread, skipping if a bot summary already exists.
        if (!HasBotSummary(existingThreads, botId))
        {
            await CreateThreadAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                BuildSummaryText(result),
                null,
                null,
                cancellationToken);
        }

        // Post each inline comment, skipping locations the bot has already covered.
        foreach (var comment in result.Comments)
        {
            CommentThreadContext? threadContext = null;
            GitPullRequestCommentThreadContext? prThreadContext = null;
            string? normalizedFilePath = null;

            if (comment.FilePath is not null)
            {
                // ADO requires paths with a leading '/'; normalize in case the AI omits it.
                normalizedFilePath = NormalizePath(comment.FilePath);
                // ADO requires Line >= 1; treat 0 (invalid) the same as null (no line anchor).
                var hasValidLine = comment.LineNumber.HasValue && comment.LineNumber.Value > 0;
                threadContext = new CommentThreadContext
                {
                    FilePath = normalizedFilePath,
                    RightFileStart = hasValidLine
                        ? new CommentPosition { Line = comment.LineNumber!.Value, Offset = 1 }
                        : null,
                    RightFileEnd = hasValidLine
                        ? new CommentPosition { Line = comment.LineNumber!.Value, Offset = 1 }
                        : null,
                };

                // pullRequestThreadContext anchors the thread to the correct iteration diff.
                if (changeTrackingIds.TryGetValue(normalizedFilePath, out var trackingId))
                {
                    prThreadContext = new GitPullRequestCommentThreadContext
                    {
                        ChangeTrackingId = trackingId,
                        IterationContext = new CommentIterationContext
                        {
                            FirstComparingIteration = (short)iterationId,
                            SecondComparingIteration = (short)iterationId,
                        },
                    };
                }
            }

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

            var severityPrefix = comment.Severity switch
            {
                CommentSeverity.Error => "ERROR",
                CommentSeverity.Warning => "WARNING",
                CommentSeverity.Suggestion => "SUGGESTION",
                _ => "INFO",
            };

            var sanitizedMessage = HtmlSanitizer.Sanitize(comment.Message);

            await CreateThreadAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                $"{severityPrefix}: {sanitizedMessage}",
                threadContext,
                prThreadContext,
                cancellationToken);

            diagnostics.RecordPosted();
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
        sb.Append(HtmlSanitizer.Sanitize(result.Summary));

        if (result.CarriedForwardFilePaths.Count > 0)
        {
            sb.Append($"\n\n**Carried forward unchanged files** ({result.CarriedForwardFilePaths.Count} files — results from prior review retained)\n\n");
            foreach (var path in result.CarriedForwardFilePaths)
            {
                sb.Append($"- {path}\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Returns <c>true</c> if a bot-authored PR-level summary thread already exists.
    ///     Bot authorship is determined by comparing the comment's <see cref="PrThreadComment.AuthorId" />
    ///     against the current connection's authorized identity (<paramref name="botId" />).
    /// </summary>
    internal static bool HasBotSummary(IReadOnlyList<PrCommentThread>? threads, Guid? botId)
    {
        return (threads ?? []).Any(t =>
            t.FilePath is null &&
            t.Comments.Any(c => IsBotAuthor(c.AuthorId, botId)
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
    internal static bool IsBotAuthor(Guid? authorId, Guid? botId)
    {
        return authorId.HasValue && botId.HasValue && authorId.Value == botId.Value;
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
                ThreadId = thread.ThreadId,
                Score = thread.Comments
                    .Where(comment => IsBotAuthor(comment.AuthorId, botId))
                    .Select(comment => CalculateTextSimilarity(normalizedMessage, NormalizeCommentMessage(comment.Content)))
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

    private static async Task CreateThreadAsync(
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
        await gitClient.CreateThreadAsync(
            thread,
            repositoryId,
            pullRequestId,
            projectId,
            ct);
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

        var buffer = new System.Text.StringBuilder(sanitized.Length);
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

    internal sealed record DuplicateSuppressionMatch(string ReasonCode, int ThreadId);

    private sealed class PostingDiagnosticsBuilder
    {
        private readonly Dictionary<string, int> _suppressionReasons = new(StringComparer.Ordinal);
        private readonly HashSet<string> _degradedComponents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fallbackChecks = new(StringComparer.Ordinal);
        private int _affectedCandidateCount;
        private int _postedCount;
        private int _suppressedCount;
        private bool _consideredOpenThreads;
        private bool _consideredResolvedThreads;
        private string? _degradedCause;

        public PostingDiagnosticsBuilder(int candidateCount, int carriedForwardCandidatesSkipped, bool consideredOpenThreads, bool consideredResolvedThreads)
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
                FallbackChecks = this._fallbackChecks.OrderBy(value => value, StringComparer.Ordinal).ToList().AsReadOnly(),
                DegradedComponents = this._degradedComponents.OrderBy(value => value, StringComparer.Ordinal).ToList().AsReadOnly(),
                DegradedCause = this._degradedCause,
                AffectedCandidateCount = this._affectedCandidateCount,
            };
        }
    }
}
