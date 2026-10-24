// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Reads optional fixes and binds them to exact reviewed source without rejecting findings.</summary>
public static class ReviewSuggestedFixValidator
{
    private const int MaximumSourceLines = 200;
    internal const int MaximumCodeCharacters = 12000;

    private static readonly Regex HunkHeader = new(
        @"^@@ -[0-9]+(?:,[0-9]+)? \+(?<start>[0-9]+)(?:,(?<count>[0-9]+))? @@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Parses model-owned replacement fields. Model-supplied source identity is ignored.</summary>
    public static ReviewSuggestedFix? Parse(JsonElement finding)
    {
        if (finding.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fixes = finding.EnumerateObject().Where(property => Normalize(property.Name) == "suggestedfix").ToArray();
        if (fixes.Length != 1)
        {
            return null;
        }

        var fix = fixes[0].Value;
        if (fix.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var groups = fix.EnumerateObject().GroupBy(property => Normalize(property.Name)).ToArray();
        if (groups.Any(group => group.Count() != 1))
        {
            return null;
        }

        var fields = groups.ToDictionary(group => group.Key, group => group.Single().Value);
        if (!fields.TryGetValue("startline", out var start) || start.ValueKind != JsonValueKind.Number || !start.TryGetInt32(out var startLine)
            || !fields.TryGetValue("endline", out var end) || end.ValueKind != JsonValueKind.Number || !end.TryGetInt32(out var endLine)
            || !fields.TryGetValue("originaltext", out var original) || original.ValueKind != JsonValueKind.String
            || !fields.TryGetValue("replacementtext", out var replacement) || replacement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new ReviewSuggestedFix(startLine, endLine, original.GetString()!, replacement.GetString()!);
    }

    /// <summary>Parses an optional fix from model DTO extension fields.</summary>
    public static ReviewSuggestedFix? ParseProperties(IReadOnlyDictionary<string, JsonElement>? properties) =>
        properties is null ? null : Parse(JsonSerializer.SerializeToElement(properties));

    /// <summary>Requires the fetched source commit to match the queued revision before establishing applicability.</summary>
    public static ReviewResult Bind(ReviewResult result, PullRequest pr, string? queuedHeadSha) =>
        Bind(
            result, pr.ChangedFiles, !string.IsNullOrWhiteSpace(pr.FetchedHeadSha)
                                     && string.Equals(pr.FetchedHeadSha, queuedHeadSha, StringComparison.Ordinal)
                ? pr.FetchedHeadSha
                : null);

    /// <summary>Retains all findings and validates optional replacements against the reviewed snapshot.</summary>
    public static ReviewResult Bind(ReviewResult result, IReadOnlyList<ChangedFile> files, string? headSha)
    {
        if (!result.Comments.Any(comment => comment.SuggestedFix is not null)
            && !result.WithheldFindings.Any(finding => finding.Comment.SuggestedFix is not null))
        {
            return result;
        }

        return result with
        {
            Comments = result.Comments.Select(comment => Bind(comment, files, headSha)).ToList(),
            WithheldFindings = result.WithheldFindings.Select(finding => finding with { Comment = Bind(finding.Comment, files, headSha) }).ToList(),
        };
    }

    /// <summary>Validates exact source and preserves the proposed code when it cannot be applied safely.</summary>
    public static ReviewComment Bind(ReviewComment comment, IReadOnlyList<ChangedFile> files, string? headSha)
    {
        if (comment.SuggestedFix is not { } fix)
        {
            return comment;
        }

        var unbound = fix with { IsSourceValidated = false };
        if (!ChangesSource(fix))
        {
            return comment with { SuggestedFix = unbound with { RejectionReason = "replacement_identical_to_original" } };
        }

        if (fix.IsAnchorRemapped || fix.SourceAnchorLineNumber is { } anchor && anchor != comment.LineNumber
                                 || fix.SourceFilePath is not null && fix.SourceFilePath != NormalizePath(comment.FilePath))
        {
            return comment with { SuggestedFix = unbound with { IsAnchorRemapped = true } };
        }

        if (fix.SourceHeadSha is not null && !string.Equals(fix.SourceHeadSha, headSha, StringComparison.Ordinal))
        {
            return comment with { SuggestedFix = unbound };
        }

        var matches = files.Where(candidate => NormalizePath(candidate.Path) == NormalizePath(comment.FilePath)).Take(2).ToArray();
        var file = matches.Length == 1 ? matches[0] : null;
        if (string.IsNullOrWhiteSpace(headSha) || file is null || file.IsBinary || file.ChangeType == ChangeType.Delete
            || string.IsNullOrEmpty(file.FullContent) || fix.StartLine < 1 || fix.EndLine < fix.StartLine
            || fix.EndLine - (long)fix.StartLine >= MaximumSourceLines || comment.LineNumber < fix.StartLine
            || comment.LineNumber > fix.EndLine || comment.LineNumber is null || fix.OriginalText is null || fix.ReplacementText is null
            || fix.OriginalText.Length > MaximumCodeCharacters || fix.ReplacementText.Length > MaximumCodeCharacters)
        {
            return comment with { SuggestedFix = unbound };
        }

        var lines = file.FullContent.ReplaceLineEndings("\n").Split('\n');
        var expected = fix.EndLine <= lines.Length ? string.Join('\n', lines[(fix.StartLine - 1)..fix.EndLine]) : null;
        var valid = expected is not null && string.Equals(expected, fix.OriginalText.ReplaceLineEndings("\n"), StringComparison.Ordinal)
                                         && IsVisibleRange(file.UnifiedDiff, fix.StartLine, fix.EndLine);
        return comment with
        {
            SuggestedFix = valid
                ? unbound with
                {
                    SourceHeadSha = headSha, SourceFilePath = NormalizePath(file.Path), SourceAnchorLineNumber = comment.LineNumber,
                    IsSourceValidated = true
                }
                : unbound,
        };
    }

    private static bool IsVisibleRange(string diff, int startLine, int endLine)
    {
        long currentLine = 0;
        var remainingLines = 0;
        var containsStart = false;
        foreach (var line in diff.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                containsStart = false;
                remainingLines = 0;
                var match = HunkHeader.Match(line);
                if (match.Success && int.TryParse(match.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hunkStart))
                {
                    currentLine = hunkStart;
                    if (!match.Groups["count"].Success)
                    {
                        remainingLines = 1;
                    }
                    else
                    {
                        int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out remainingLines);
                    }
                }

                continue;
            }

            if (remainingLines <= 0 || ReviewDiffProcessor.ClassifyHunkLine(line) is not (HunkLineKind.Added or HunkLineKind.Context))
            {
                continue;
            }

            containsStart |= currentLine == startLine;
            if (containsStart && currentLine == endLine)
            {
                return true;
            }

            currentLine++;
            remainingLines--;
        }

        return false;
    }

    internal static string? NormalizePath(string? path) => path?.Replace('\\', '/').TrimStart('/');

    /// <summary>Requires a change after normalizing line endings without removing semantic whitespace.</summary>
    public static bool ChangesSource(ReviewSuggestedFix fix) =>
        fix.OriginalText is not null && fix.ReplacementText is not null
                                     && !string.Equals(
                                         fix.OriginalText.ReplaceLineEndings("\n"), fix.ReplacementText.ReplaceLineEndings("\n"), StringComparison.Ordinal);

    private static string Normalize(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
