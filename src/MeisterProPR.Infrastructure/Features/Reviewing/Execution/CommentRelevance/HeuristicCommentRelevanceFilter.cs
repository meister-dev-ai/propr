// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
        // Only language-agnostic, mechanical signals remain here: a mismatched file/line anchor, a
        // structurally cross-file claim (two or more distinct file references), and the absence of any
        // concrete observable (code token / line reference). Text-shaped hedge/vague/tooling/severity
        // screening now lives in the embedding-based semantic comment screener.
        var reasonCodes = new HashSet<string>(StringComparer.Ordinal);

        if (IsWrongFileOrAnchor(request, comment))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.WrongFileOrAnchor);
        }

        if (LooksLikeCrossFileReference(comment.Message))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.UnverifiableCrossFileClaim);
        }

        if (IsMissingConcreteObservable(comment))
        {
            reasonCodes.Add(CommentRelevanceReasonCodes.MissingConcreteObservable);
        }

        if (reasonCodes.Count == 0)
        {
            return Keep(comment);
        }

        if (allowAmbiguous && IsAmbiguousReasonSet(reasonCodes))
        {
            return new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.KeepDecision,
                comment,
                reasonCodes.ToArray(),
                CommentRelevanceFilterDecision.FallbackModeSource);
        }

        return Discard(comment, reasonCodes);
    }

    private static bool IsAmbiguousReasonSet(IEnumerable<string> reasonCodes)
    {
        return reasonCodes.All(code => code is CommentRelevanceReasonCodes.UnverifiableCrossFileClaim
            or CommentRelevanceReasonCodes.MissingConcreteObservable);
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

                DiscardIfDuplicate(decisions, i, j);
            }
        }
    }

    private static void DiscardIfDuplicate(IList<CommentRelevanceFilterDecision> decisions, int i, int j)
    {
        var left = decisions[i].OriginalComment;
        var right = decisions[j].OriginalComment;
        if (!string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (CalculateSimilarity(left.Message, right.Message) < 0.72d)
        {
            return;
        }

        var leftScore = GetStrengthScore(left);
        var rightScore = GetStrengthScore(right);
        var discardIndex = leftScore >= rightScore ? j : i;
        var discardedComment = decisions[discardIndex].OriginalComment;
        decisions[discardIndex] = new CommentRelevanceFilterDecision(
            CommentRelevanceFilterDecision.DiscardDecision,
            discardedComment,
            [CommentRelevanceReasonCodes.DuplicateLocalPattern],
            CommentRelevanceFilterDecision.DeterministicScreeningSource);
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

    private static bool IsWrongFileOrAnchor(CommentRelevanceFilterRequest request, ReviewComment comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.FilePath) &&
            !string.Equals(
                NormalizeComparablePath(comment.FilePath),
                NormalizeComparablePath(request.FilePath),
                StringComparison.OrdinalIgnoreCase))
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

    // Normalizes a repo-relative path for equality comparison: unifies separators and trims a leading
    // slash. A finding anchored to "src/Foo.cs" and a file under review recorded as "/src/Foo.cs" denote
    // the same file, so the anchor check must not treat that leading-slash difference as a wrong file.
    private static string NormalizeComparablePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').TrimStart('/');
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
        // Two or more DISTINCT file references signal a genuinely cross-file claim. Counting distinct
        // paths rather than raw matches keeps a finding that names its own single file more than once
        // from being misread as spanning multiple files.
        return FileReferenceRegex().Matches(message)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() >= 2;
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

    // Matches a real file reference — a bare file name or a repo-relative path ending in a known
    // source-file extension (GetFileDiffHandler.cs, src/Foo/Bar.cs, RetainedThreadPanel.vue). Matching
    // is case-sensitive on purpose: source-file extensions are lowercase by convention, while member
    // access in code findings is PascalCase (job.IterationId, settings.Json), so case alone rejects the
    // member-access expressions that the previous any-dotted-token pattern miscounted as cross-file.
    [GeneratedRegex(
        @"\b[A-Za-z0-9_\-]+(?:[\\/][A-Za-z0-9_\-]+)*\.(?:cs|vue|ts|tsx|js|jsx|mjs|cjs|razor|cshtml|css|scss|less|json|ya?ml|xml|md|csproj|slnx?|props|targets|config|sql|sh|ps1|py|go|rs|java|kt|rb|php|html?|toml|ini)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex FileReferenceRegex();
}
