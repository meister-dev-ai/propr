// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Builds the bounded intent inputs of the evidence-backed judge: the pull request title and description, the
///     linked work items, and the diff hunk at the claim's anchor. Each input has a character cap. A truncated text
///     keeps its head, or for the diff hunk the part around the anchor line, and carries an explicit truncation marker,
///     so the judge can tell that text was omitted. Linked items beyond the item cap are counted, not shown.
/// </summary>
internal static partial class EvidenceJudgeInput
{
    internal const int MaxTitleChars = 300;
    internal const int MaxDescriptionChars = 3000;
    internal const int MaxDiffHunkChars = 4000;
    internal const int MaxLinkedItems = 5;
    internal const int MaxLinkedItemDescriptionChars = 1200;
    internal const int MaxLinkedItemLabelChars = 80;
    internal const string TruncationMarker = "…(truncated)";

    internal static PromptTemplateModels.EvidenceVerificationUserModel BuildUserModel(
        ClaimDescriptor claim,
        string anchorSource,
        int sourceStartLine,
        ReviewVerificationIntent? intent)
    {
        // A token generated per call marks the BEGIN and END lines of the untrusted text. Author-written text cannot
        // predict it, so a forged END line inside that text does not end the quoted section.
        var boundary = Guid.NewGuid().ToString("N");
        var description = Untrusted(TruncateHead(intent?.PullRequestDescription, MaxDescriptionChars), boundary);
        var diffHunk = IsReviewedFile(claim.AnchorFilePath, intent?.ReviewedFilePath)
            ? ExtractAnchorHunk(intent!.ReviewedFileDiff, claim.AnchorLineNumber, MaxDiffHunkChars)
            : null;
        var linkedItems = intent?.LinkedItems ?? [];
        var shownItems = linkedItems
            .Take(MaxLinkedItems)
            .Select(item =>
            {
                var itemDescription = Untrusted(TruncateHead(item.Description, MaxLinkedItemDescriptionChars), boundary);
                return new PromptTemplateModels.EvidenceVerificationLinkedItemModel(
                    Untrusted(TruncateHead(SingleLine(item.ItemType), MaxLinkedItemLabelChars), boundary) ?? string.Empty,
                    Untrusted(TruncateHead(SingleLine(item.ProviderKey), MaxLinkedItemLabelChars), boundary) ?? string.Empty,
                    Untrusted(TruncateHead(SingleLine(item.Title), MaxTitleChars), boundary) ?? string.Empty,
                    itemDescription is not null,
                    itemDescription);
            })
            .ToList();
        var omittedItemCount = Math.Max(0, linkedItems.Count - MaxLinkedItems);

        return new PromptTemplateModels.EvidenceVerificationUserModel(
            claim.AssertionText,
            string.IsNullOrWhiteSpace(claim.SubjectIdentifier) ? "(none)" : claim.SubjectIdentifier,
            claim.AnchorFilePath ?? string.Empty,
            claim.AnchorLineNumber?.ToString() ?? "(unknown)",
            sourceStartLine,
            anchorSource,
            Untrusted(TruncateHead(SingleLine(intent?.PullRequestTitle), MaxTitleChars), boundary) ?? "(none)",
            description is not null,
            description,
            shownItems.Count > 0,
            shownItems,
            omittedItemCount > 0,
            omittedItemCount,
            diffHunk is not null,
            diffHunk is { ContainsAnchor: false },
            diffHunk?.Text,
            boundary);
    }

    // Removes the boundary token from untrusted text, so the text can never reproduce a real END line.
    private static string? Untrusted(string? text, string boundary)
    {
        return text?.Replace(boundary, string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns the trimmed text, cut to <paramref name="maxChars" /> with a truncation marker when it is longer,
    ///     or <see langword="null" /> when it is empty.
    /// </summary>
    internal static string? TruncateHead(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        var cut = AvoidSplitSurrogateEnd(trimmed, maxChars);
        return string.Concat(trimmed.AsSpan(0, cut), "\n", TruncationMarker);
    }

    /// <summary>
    ///     Returns the hunk of <paramref name="unifiedDiff" /> whose new-side range contains the anchor line, or the
    ///     nearest hunk when none contains it, or the first hunk when the anchor line is unknown, together with whether
    ///     the returned hunk contains the anchor line. A hunk longer than <paramref name="maxChars" /> keeps its header
    ///     and the whole body lines around the anchor line that fit. Returns <see langword="null" /> for an empty diff and for a diff without a parseable hunk header,
    ///     such as a binary-file notice.
    /// </summary>
    internal static AnchorHunk? ExtractAnchorHunk(string? unifiedDiff, int? anchorLine, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return null;
        }

        var hunks = SplitHunks(unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (hunks.Count == 0)
        {
            return null;
        }

        var chosen = hunks[0];
        if (anchorLine is int line && line > 0)
        {
            chosen = hunks.MinBy(hunk => DistanceToRange(line, hunk.NewStart, hunk.NewEnd))!;
        }

        // A hunk with no new-side lines, a pure deletion, contains no anchor line.
        var containsAnchor = chosen.HasNewLines && anchorLine is int anchor && anchor >= chosen.NewStart && anchor <= chosen.NewEnd;
        return new AnchorHunk(SliceAroundAnchorLine(chosen.Lines, AnchorLineIndex(chosen, anchorLine), maxChars), containsAnchor);
    }

    private static bool IsReviewedFile(string? anchorFilePath, string? reviewedFilePath)
    {
        return !string.IsNullOrWhiteSpace(anchorFilePath)
               && !string.IsNullOrWhiteSpace(reviewedFilePath)
               && string.Equals(
                   ReviewDiffProcessor.NormalizeReviewPath(anchorFilePath),
                   ReviewDiffProcessor.NormalizeReviewPath(reviewedFilePath),
                   StringComparison.Ordinal);
    }

    private static List<DiffHunk> SplitHunks(string diff)
    {
        var hunks = new List<DiffHunk>();
        DiffHunk? current = null;
        foreach (var line in diff.Split('\n'))
        {
            var header = HunkHeaderPattern().Match(line);
            if (header.Success)
            {
                // A header whose numbers do not fit an int is malformed; its lines are skipped until the next header.
                current = TryParseRange(header, out var newStart, out var newEnd, out var hasNewLines)
                    ? new DiffHunk(newStart, newEnd, hasNewLines, [line])
                    : null;
                if (current is not null)
                {
                    hunks.Add(current);
                }

                continue;
            }

            // Lines before the first hunk header are file headers and carry no code.
            current?.Lines.Add(line);
        }

        foreach (var hunk in hunks)
        {
            while (hunk.Lines.Count > 1 && hunk.Lines[^1].Length == 0)
            {
                hunk.Lines.RemoveAt(hunk.Lines.Count - 1);
            }
        }

        return hunks;
    }

    private static bool TryParseRange(Match header, out int newStart, out int newEnd, out bool hasNewLines)
    {
        newEnd = 0;
        hasNewLines = false;
        if (!int.TryParse(header.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out newStart))
        {
            return false;
        }

        var newCount = 1;
        if (header.Groups["count"].Success
            && !int.TryParse(header.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out newCount))
        {
            return false;
        }

        hasNewLines = newCount > 0;
        newEnd = (int)Math.Min(int.MaxValue, (long)newStart + Math.Max(newCount, 1) - 1);
        return true;
    }

    private static string? SingleLine(string? text)
    {
        return text?.ReplaceLineEndings(" ");
    }

    // Moves a cut end back by one when it would separate a surrogate pair, so no lone surrogate is left.
    private static int AvoidSplitSurrogateEnd(string text, int end)
    {
        return end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) ? end - 1 : end;
    }

    private static int DistanceToRange(int line, int start, int end)
    {
        if (line < start)
        {
            return start - line;
        }

        return line > end ? line - end : 0;
    }

    // Character offset of the hunk line that holds the anchor line on the new side, or zero when the anchor line is
    // unknown or not inside the hunk.
    // Index in the hunk's lines of the line that holds the anchor line on the new side, or the first body line when the
    // anchor line is unknown or not inside the hunk.
    private static int AnchorLineIndex(DiffHunk hunk, int? anchorLine)
    {
        if (anchorLine is not int line || line < hunk.NewStart || line > hunk.NewEnd)
        {
            return 1;
        }

        var newLine = hunk.NewStart;
        for (var index = 1; index < hunk.Lines.Count; index++)
        {
            // Some diff producers strip the leading space of an empty context line; such a line is still context.
            var kind = hunk.Lines[index].Length == 0 ? HunkLineKind.Context : ReviewDiffProcessor.ClassifyHunkLine(hunk.Lines[index]);
            if (kind is HunkLineKind.Added or HunkLineKind.Context)
            {
                if (newLine == line)
                {
                    return index;
                }

                newLine++;
            }
        }

        return 1;
    }

    // Keeps the hunk header and as many whole body lines around the anchor line as fit in maxChars, so every kept
    // line keeps its diff prefix. Each side where lines were dropped is marked with the truncation marker. An anchor
    // line that alone exceeds the budget is cut from its end.
    private static string SliceAroundAnchorLine(List<string> lines, int anchorIndex, int maxChars)
    {
        var total = lines.Sum(line => line.Length + 1) - 1;
        if (total <= maxChars || lines.Count < 2)
        {
            return string.Join('\n', lines);
        }

        var header = lines[0];
        var budget = Math.Max(0, maxChars - header.Length - 1);
        anchorIndex = Math.Clamp(anchorIndex, 1, lines.Count - 1);
        var anchorText = lines[anchorIndex];
        if (anchorText.Length > budget)
        {
            var cut = AvoidSplitSurrogateEnd(anchorText, budget);
            anchorText = anchorText[..cut] + TruncationMarker;
        }

        var first = anchorIndex;
        var last = anchorIndex;
        var used = anchorText.Length;
        var grew = true;
        while (grew)
        {
            grew = false;
            if (first > 1 && used + lines[first - 1].Length + 1 <= budget)
            {
                first--;
                used += lines[first].Length + 1;
                grew = true;
            }

            if (last < lines.Count - 1 && used + lines[last + 1].Length + 1 <= budget)
            {
                last++;
                used += lines[last].Length + 1;
                grew = true;
            }
        }

        var kept = new List<string> { header };
        if (first > 1)
        {
            kept.Add(TruncationMarker);
        }

        for (var index = first; index <= last; index++)
        {
            kept.Add(index == anchorIndex ? anchorText : lines[index]);
        }

        if (last < lines.Count - 1)
        {
            kept.Add(TruncationMarker);
        }

        return string.Join('\n', kept);
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeaderPattern();

    private sealed record DiffHunk(int NewStart, int NewEnd, bool HasNewLines, List<string> Lines);

    /// <summary>A bounded diff hunk and whether its new-side range contains the anchor line.</summary>
    internal sealed record AnchorHunk(string Text, bool ContainsAnchor);
}
