// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

internal sealed partial class HeuristicCommentRelevanceFilter : ICommentRelevanceFilter
{
    private static readonly HashSet<string> DuplicateStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "be", "been", "being",
        "in", "of", "to", "for", "on", "at", "by", "or", "and", "but",
        "this", "that", "it", "its", "if", "as", "with", "from",
        "not", "may", "can", "should", "could", "would", "will",
        "you", "your",
    };

    private static readonly ImmutableArray<string> HedgeTerms =
    [
        "likely", "probably", "possibly", "might", "maybe", "appears to", "seems to", "could be",
        "may be", "i suspect", "cannot confirm", "not sure", "unclear whether",
    ];

    private static readonly ImmutableArray<string> NonActionableSuggestionTerms =
    [
        "consider", "you may want", "you might want", "it may help", "could be improved",
        "might be worth", "nice to have",
    ];

    private static readonly ImmutableArray<string> SummaryOnlyTerms =
    [
        "overall", "in general", "generally", "throughout this file", "across this file", "multiple places",
        "several issues", "broadly speaking",
    ];

    private static readonly ImmutableArray<string> CrossFileTerms =
    [
        "another file", "other file", "elsewhere", "upstream", "downstream", "caller", "callee",
        "cross-file", "across files", "outside this file",
    ];

    private static readonly ImmutableArray<string> ToolingTerms =
    [
        "tooling limitation", "truncated", "unable to inspect", "could not inspect", "cannot inspect",
        "stale context", "incomplete context", "unable to load",
    ];

    private static readonly ImmutableArray<string> SevereClaimTerms =
    [
        "critical", "catastrophic", "guaranteed", "always fails", "will crash", "security vulnerability",
        "data loss", "definitely broken",
    ];

    public string ImplementationId => "heuristic-v1";

    public string ImplementationVersion => "1.0.0";

    public Task<CommentRelevanceFilterResult> FilterAsync(
        CommentRelevanceFilterRequest request,
        CancellationToken ct = default)
    {
        var decisions = request.Comments
            .Select(comment => EvaluateComment(request, comment, false))
            .ToList();

        ApplyDuplicateLocalPattern(decisions);

        return Task.FromResult(
            new CommentRelevanceFilterResult(
                this.ImplementationId,
                this.ImplementationVersion,
                request.FilePath,
                request.Comments.Count,
                decisions.AsReadOnly()));
    }

    internal static CommentRelevanceFilterDecision EvaluateComment(
        CommentRelevanceFilterRequest request,
        ReviewComment comment,
        bool allowAmbiguous)
    {
        var reasonCodes = new HashSet<string>(StringComparer.Ordinal);
        var message = comment.Message;

        if (ContainsAny(message, HedgeTerms))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.HedgingLanguage);
        }

        if (comment.Severity == CommentSeverity.Suggestion && ContainsAny(message, NonActionableSuggestionTerms))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.NonActionableSuggestion);
        }

        if (IsWrongFileOrAnchor(request, comment))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.WrongFileOrAnchor);
        }

        if (ContainsAny(message, ToolingTerms))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.ToolingLimitationMisclassified);
        }

        if (IsSummaryLevelOnly(comment))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.SummaryLevelOnly);
        }

        var hasCrossFileClaim = ContainsAny(message, CrossFileTerms) || LooksLikeCrossFileReference(message);
        if (hasCrossFileClaim)
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.UnverifiableCrossFileClaim);
        }

        var missingConcreteObservable = IsMissingConcreteObservable(comment);
        if (missingConcreteObservable)
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.MissingConcreteObservable);
        }

        if (comment.Severity is CommentSeverity.Error or CommentSeverity.Warning &&
            ContainsAny(message, SevereClaimTerms) &&
            (missingConcreteObservable || hasCrossFileClaim))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.SeverityOverstated);
        }

        if (reasonCodes.Count == 0)
        {
            return Keep(comment);
        }

        if (allowAmbiguous &&
            reasonCodes.All(code => code is CommentRelevanceReasonCodes.UnverifiableCrossFileClaim
                or CommentRelevanceReasonCodes.SeverityOverstated
                or CommentRelevanceReasonCodes.MissingConcreteObservable))
        {
            return new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.KeepDecision,
                comment,
                reasonCodes.ToArray(),
                CommentRelevanceFilterDecision.FallbackModeSource);
        }

        return Discard(comment, reasonCodes);
    }

    internal static void ApplyDuplicateLocalPattern(IList<CommentRelevanceFilterDecision> decisions)
    {
        for (var i = 0; i < decisions.Count; i++)
        {
            if (!decisions[i].IsKeep)
            {
                continue;
            }

            for (var j = i + 1; j < decisions.Count; j++)
            {
                if (!decisions[j].IsKeep)
                {
                    continue;
                }

                var left = decisions[i].OriginalComment;
                var right = decisions[j].OriginalComment;
                if (!string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CalculateSimilarity(left.Message, right.Message) < 0.72d)
                {
                    continue;
                }

                var leftScore = GetStrengthScore(left);
                var rightScore = GetStrengthScore(right);
                var discardIndex = leftScore >= rightScore ? j : i;
                var keepIndex = discardIndex == i ? j : i;
                var discardedComment = decisions[discardIndex].OriginalComment;
                decisions[discardIndex] = new CommentRelevanceFilterDecision(
                    CommentRelevanceFilterDecision.DiscardDecision,
                    discardedComment,
                    [CommentRelevanceReasonCodes.DuplicateLocalPattern],
                    CommentRelevanceFilterDecision.DeterministicScreeningSource);

                i = Math.Min(i, keepIndex);
            }
        }
    }

    private static CommentRelevanceFilterDecision Keep(ReviewComment comment)
    {
        return new CommentRelevanceFilterDecision(
            CommentRelevanceFilterDecision.KeepDecision,
            comment,
            [],
            CommentRelevanceFilterDecision.DeterministicScreeningSource);
    }

    private static CommentRelevanceFilterDecision Discard(ReviewComment comment, IEnumerable<string> reasonCodes)
    {
        return new CommentRelevanceFilterDecision(
            CommentRelevanceFilterDecision.DiscardDecision,
            comment,
            reasonCodes.Distinct(StringComparer.Ordinal).ToArray(),
            CommentRelevanceFilterDecision.DeterministicScreeningSource);
    }

    private static bool ContainsAny(string message, IEnumerable<string> fragments)
    {
        return fragments.Any(fragment => message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWrongFileOrAnchor(CommentRelevanceFilterRequest request, ReviewComment comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.FilePath) &&
            !string.Equals(comment.FilePath, request.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!comment.LineNumber.HasValue || comment.LineNumber.Value < 1)
        {
            return false;
        }

        var totalLines = CountLines(request.File.FullContent);
        return totalLines > 0 && comment.LineNumber.Value > totalLines;
    }

    private static bool IsSummaryLevelOnly(ReviewComment comment)
    {
        return !comment.LineNumber.HasValue && ContainsAny(comment.Message, SummaryOnlyTerms);
    }

    private static bool IsMissingConcreteObservable(ReviewComment comment)
    {
        if (comment.LineNumber.HasValue)
        {
            return false;
        }

        return !ConcreteObservableRegex().IsMatch(comment.Message);
    }

    private static bool LooksLikeCrossFileReference(string message)
    {
        return FileReferenceRegex().Matches(message).Count >= 2;
    }

    private static double CalculateSimilarity(string left, string right)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = leftTokens.Intersect(rightTokens).Count();
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0d : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'');
            if (isWordChar)
            {
                continue;
            }

            if (i > start)
            {
                var token = text[start..i].ToLowerInvariant();
                if (token.Length >= 3 && !DuplicateStopWords.Contains(token))
                {
                    tokens.Add(token);
                }
            }

            start = i + 1;
        }

        return tokens;
    }

    private static int GetStrengthScore(ReviewComment comment)
    {
        var severityScore = comment.Severity switch
        {
            CommentSeverity.Error => 4,
            CommentSeverity.Warning => 3,
            CommentSeverity.Suggestion => 2,
            _ => 1,
        };

        return severityScore * 1000 + comment.Message.Length;
    }

    private static int CountLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        var count = 1;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex(@"(`[^`]+`|\b[A-Z][A-Za-z0-9_]+\b|\b[a-z_]+\([^\)]*\)|:[Ll]\d+|\bline\s+\d+\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ConcreteObservableRegex();

    [GeneratedRegex(@"[A-Za-z0-9_./\\-]+\.[A-Za-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex FileReferenceRegex();
}
