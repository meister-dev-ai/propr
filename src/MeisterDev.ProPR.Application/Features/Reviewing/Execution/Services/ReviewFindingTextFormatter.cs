// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Renders finding classification and risk evidence without changing the raw finding message.</summary>
public static class ReviewFindingTextFormatter
{
    private const string StartMarker = "<!-- propr-risk-assessment:start -->";
    private const string EndMarker = "<!-- propr-risk-assessment:end -->";
    private const string TableHeader = "| Assessment | Level | Reason |";
    private const string TableSeparator = "| --- | --- | --- |";
    private const string DetailsStart = "<details>\n<summary>Assessment details</summary>\n\n";
    private const string DetailsEnd = "\n\n</details>";
    private const string FenceRepairMarker = "<!-- propr-fence-repair -->";

    private static readonly MarkdownPipeline DescriptionPipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UsePipeTables(new PipeTableOptions { UseGfmRules = true })
        .Build();

    /// <summary>Formats a finding for SCM publication, using an already rendered message when provided.</summary>
    public static string Format(
        ReviewComment comment,
        string? renderedMessage = null,
        string? classificationLabel = null,
        bool collapseAssessment = false,
        Func<string, string>? renderText = null,
        bool htmlAssessment = false,
        FindingBadgeStyle badgeStyle = FindingBadgeStyle.ShieldsIo)
    {
        ArgumentNullException.ThrowIfNull(comment);
        var message = CloseOpenFences(renderedMessage ?? (ReviewSuggestedFixRenderer.RenderMessage(comment, false)));
        renderText ??= RenderPlainText;
        var label = classificationLabel ?? comment.Severity switch
        {
            CommentSeverity.Error => "Error",
            CommentSeverity.Warning => "Warning",
            CommentSeverity.Suggestion => "Suggestion",
            _ => "Info",
        };
        var risk = comment.RiskAssessment;
        var header = FindingBadgeHeaderFormatter.Format(label, risk, FindingTypeLabels(comment.FindingClassification), badgeStyle);
        string? assessment = null;
        if (risk is not null)
        {
            assessment = htmlAssessment
                ? HtmlFindingAssessmentFormatter.Format(risk)
                : FormatMarkdownAssessment(risk, collapseAssessment, renderText);
        }

        return ReviewPresentationTemplateCatalog.Shared.Render(
            ReviewPresentationTemplate.Finding, new ReviewPresentationModels.Finding(header, message, assessment));
    }

    private static string FormatMarkdownAssessment(FindingRiskAssessment risk, bool collapsed, Func<string, string> renderText)
    {
        var likelihood = risk.Likelihood == FindingLikelihood.NotApplicable ? "Not applicable" : FormatLevel(risk.Likelihood);
        var riskReason = risk.Severity == FindingRiskSeverity.Unknown
            ? "No scored impact and likelihood assessment is available."
            : "Derived from impact and likelihood.";
        return ReviewPresentationTemplateCatalog.Shared.Render(
            ReviewPresentationTemplate.MarkdownAssessment,
            new ReviewPresentationModels.Assessment(
            [
                new("Risk", FormatLevel(risk.Severity), riskReason),
                new("Impact", FormatLevel(risk.Impact), RenderCell(risk.ImpactReason ?? "Not assessed.", renderText)),
                new("Likelihood", likelihood, RenderCell(risk.LikelihoodReason ?? "Not assessed.", renderText)),
                new("Confidence", FormatLevel(risk.Confidence), RenderCell(risk.ConfidenceReason ?? "Not assessed.", renderText)),
            ], collapsed));
    }

    private static IReadOnlyList<string> FindingTypeLabels(FindingTypeClassification? classification)
    {
        var normalized = FindingTypeClassification.Merge([classification]);
        return normalized is null
            ? []
            : normalized.CoreSlugs.Select(slug => FindingTypeTaxonomy.Find(slug)!.DisplayName)
                .Concat(normalized.CustomTags.Select(tag => tag.DisplayName)).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Restores the severity-prefixed raw claim from a finding comment that ProPR published with a badge header, so a
    ///     re-review recognizes its own earlier finding. Other text is returned unchanged.
    /// </summary>
    public static string StripAssessment(string text)
    {
        var trimmed = text.TrimEnd();
        if (!trimmed.EndsWith(EndMarker, StringComparison.Ordinal))
        {
            return StripClassificationOnly(text);
        }

        var start = -1;
        var contentStart = -1;
        foreach (var prefix in new[] { "\n\n" + StartMarker + "\n", "\r\n\r\n" + StartMarker + "\r\n" })
        {
            var candidate = trimmed.LastIndexOf(prefix, StringComparison.Ordinal);
            if (candidate > start)
            {
                start = candidate;
                contentStart = candidate + prefix.Length;
            }
        }

        if (start < 0)
        {
            return text;
        }

        var assessment = trimmed[contentStart..^EndMarker.Length].Trim('\r', '\n').ReplaceLineEndings("\n");
        if (assessment.StartsWith(DetailsStart, StringComparison.Ordinal) && assessment.EndsWith(DetailsEnd, StringComparison.Ordinal))
        {
            assessment = assessment[DetailsStart.Length..^DetailsEnd.Length];
        }

        // The levels of the badge header and of the assessment table must agree, so a quoted example stays text.
        var body = trimmed[..start];
        if (!TryReadBadgeHeader(body, out var header, out var bodyStart)
            || header.Levels is null
            || !(IsAssessmentTable(assessment, out var levels) || HtmlFindingAssessmentFormatter.TryReadLevels(assessment, out levels))
            || !header.Levels.SequenceEqual(levels))
        {
            return text;
        }

        return header.Severity.ToUpperInvariant() + ": " + RestoreFindingText(body[bodyStart..]);
    }

    private static string StripClassificationOnly(string text) =>
        TryReadBadgeHeader(text, out var header, out var bodyStart) && header.Levels is null
            ? header.Severity.ToUpperInvariant() + ": " + RestoreFindingText(text[bodyStart..])
            : text;

    // Remove generated fence repair before restoring neutralized literal marker examples.
    private static string RestoreFindingText(string text) =>
        StripFenceRepair(ReviewSuggestedFixRenderer.StripGeneratedFix(text)).Replace("<\u200B", "<", StringComparison.Ordinal);

    /// <summary>Closes a genuine open top-level fence in rendered text before another Markdown section is appended.</summary>
    public static string CloseOpenFences(string renderedText)
    {
        var document = Markdown.Parse(renderedText, DescriptionPipeline);
        if (document.LastOrDefault() is not FencedCodeBlock { ClosingFencedCharCount: 0 } fence)
        {
            return renderedText;
        }

        return renderedText + "\n" + new string(fence.FencedChar, fence.OpeningFencedCharCount) + "\n" + FenceRepairMarker;
    }

    private static string StripFenceRepair(string text)
    {
        if (!text.EndsWith(FenceRepairMarker, StringComparison.Ordinal))
        {
            return text;
        }

        var markerBreak = text.LastIndexOf('\n');
        var fenceBreak = markerBreak < 1 ? -1 : text.LastIndexOf('\n', markerBreak - 1);
        if (fenceBreak < 0)
        {
            return text;
        }

        var start = fenceBreak > 0 && text[fenceBreak - 1] == '\r' ? fenceBreak - 1 : fenceBreak;
        var candidate = text[..start];
        return CloseOpenFences(candidate).ReplaceLineEndings("\n") == text.ReplaceLineEndings("\n") ? candidate : text;
    }

    /// <summary>Bounds recognized finding metadata before a discussion is bounded for review memory.</summary>
    public static string CompactBadgeHeaderForHistory(string text)
    {
        return TryReadBadgeHeader(text, out var header, out _)
            ? header.CompactText + text[text.IndexOfAny(['\r', '\n'])..]
            : text;
    }

    private static bool TryReadBadgeHeader(string text, out FindingBadgeHeaderFormatter.Header header, out int bodyStart)
    {
        var separator = Regex.Match(text, @"\r\n\r\n|\n\n|\r\r", RegexOptions.CultureInvariant);
        bodyStart = separator.Success ? separator.Index + separator.Length : 0;
        header = null!;
        return separator.Success && FindingBadgeHeaderFormatter.TryRead(text[..separator.Index], out header);
    }

    /// <summary>Bounds a generated header and its separator without changing the finding body.</summary>
    public static string LimitBadgeHeader(string text, int maximumHeaderLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumHeaderLength);
        if (!TryReadBadgeHeader(text, out _, out var bodyStart))
        {
            return text;
        }

        var headerEnd = text.IndexOfAny(['\r', '\n']);
        var separatorLength = bodyStart - headerEnd;
        var header = FindingBadgeHeaderFormatter.Limit(text[..headerEnd], Math.Max(0, maximumHeaderLength - separatorLength));
        return header.Length == 0 ? text[bodyStart..] : header + text[headerEnd..];
    }

    /// <summary>Bounds a published body without splitting generated metadata badges or applicable suggestions.</summary>
    public static string Truncate(string text, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        if (text.Length <= maximumLength || !TryReadBadgeHeader(text, out _, out var bodyStart))
        {
            return TruncateBody(text, maximumLength);
        }

        var headerEnd = text.IndexOfAny(['\r', '\n']);
        var header = FindingBadgeHeaderFormatter.Limit(text[..headerEnd], maximumLength / 3);
        var prefix = header.Length == 0 ? string.Empty : header + "\n\n";
        return prefix + TruncateBody(text[bodyStart..], maximumLength - prefix.Length);
    }

    private static string TruncateBody(string text, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        if (text.Length <= maximumLength)
        {
            return text;
        }

        if (ReviewSuggestedFixRenderer.TryLocateGeneratedFix(text, out var fixStart, out var fixEnd))
        {
            var fix = text[fixStart..fixEnd];
            var suffix = ReviewSuggestedFixRenderer.DowngradeUnstructuredSuggestions(text[fixEnd..]);
            if (fix.Length > maximumLength)
            {
                fix = ReviewSuggestedFixRenderer.DowngradeUnstructuredSuggestions(fix);
            }

            if (fix.Length <= maximumLength)
            {
                if (fix.Length + suffix.Length >= maximumLength)
                {
                    suffix = string.Empty;
                }

                var prefix = ReviewSuggestedFixRenderer.DowngradeUnstructuredSuggestions(text[..fixStart]);
                return TruncateMarkdownSection(prefix, maximumLength - fix.Length - suffix.Length) + fix + suffix;
            }
        }

        // A shortened replacement must never remain an applicable edit.
        text = ReviewSuggestedFixRenderer.DowngradeUnstructuredSuggestions(text);
        if (text.Length <= maximumLength)
        {
            return text;
        }

        var cutoff = maximumLength;
        var closing = string.Empty;
        var start = text.LastIndexOf("\n\n" + StartMarker + "\n\n", StringComparison.Ordinal);
        var contentStart = start + StartMarker.Length + 4;
        if (start >= 0 && text.EndsWith("\n\n" + EndMarker, StringComparison.Ordinal))
        {
            var assessment = text[contentStart..^(EndMarker.Length + 2)];
            if (HtmlFindingAssessmentFormatter.TryReadLevels(assessment, out _))
            {
                var suffix = text[start..];
                var prefix = text[..start];
                var minimumDescription = Math.Min(prefix.Length, 200);
                return maximumLength - suffix.Length >= minimumDescription
                    ? TruncateMarkdownSection(prefix, maximumLength - suffix.Length) + suffix
                    : TruncateMarkdownSection(prefix, maximumLength);
            }

            var collapsed = assessment.StartsWith(DetailsStart, StringComparison.Ordinal)
                            && assessment.EndsWith(DetailsEnd, StringComparison.Ordinal);
            var table = collapsed ? assessment[DetailsStart.Length..^DetailsEnd.Length] : assessment;
            if (IsAssessmentTable(table, out _) && cutoff > start)
            {
                closing = (collapsed ? DetailsEnd : string.Empty) + "\n\n" + EndMarker;
                cutoff = Math.Min(maximumLength - closing.Length, text.Length - closing.Length);
                contentStart += collapsed ? DetailsStart.Length : 0;
                if (cutoff <= contentStart)
                {
                    cutoff = Math.Min(start, maximumLength);
                    closing = string.Empty;
                }
            }
        }

        var boundary = text.LastIndexOf(' ', cutoff);
        if (boundary < 1)
        {
            boundary = cutoff;
        }

        if (closing.Length > 0 && boundary < contentStart)
        {
            boundary = start;
            closing = string.Empty;
        }

        if (boundary > 0 && char.IsHighSurrogate(text[boundary - 1]))
        {
            boundary--;
        }

        return closing.Length > 0 ? text[..boundary].TrimEnd() + closing : TruncateMarkdownSection(text[..boundary].TrimEnd(), maximumLength);
    }

    private static string TruncateMarkdownSection(string text, int maximumLength)
    {
        text = StripFenceRepair(text);
        var cutoff = Math.Min(text.Length, maximumLength);
        while (true)
        {
            var boundary = cutoff == text.Length ? cutoff : text.LastIndexOf(' ', cutoff);
            if (boundary < 1)
            {
                boundary = cutoff;
            }

            if (boundary > 0 && char.IsHighSurrogate(text[boundary - 1]))
            {
                boundary--;
            }

            var rendered = CloseOpenFences(RenderText(text[..boundary].TrimEnd()));
            if (rendered.Length <= maximumLength)
            {
                return rendered;
            }

            cutoff = Math.Max(0, boundary - (rendered.Length - maximumLength));
        }
    }

    internal static string FormatLevel<T>(T value) where T : struct, Enum =>
        EqualityComparer<T>.Default.Equals(value, default) ? "Unassessed" : value.ToString();

    /// <summary>Neutralizes HTML in descriptions while preserving Markdown code, link destinations and autolinks.</summary>
    public static string RenderText(string text)
    {
        if (!text.Contains('<', StringComparison.Ordinal))
        {
            return text;
        }

        var originalDocument = Markdown.Parse(text, DescriptionPipeline);
        var autolinks = new bool[text.Length];
        foreach (var node in originalDocument.Descendants())
        {
            if (node is not AutolinkInline)
            {
                continue;
            }

            MarkPositions(autolinks, node.Span.Start, node.Span.End);
        }

        var inserted = new List<(int RenderedIndex, int SourceIndex)>();
        var candidate = NeutralizeHtml(text, autolinks, inserted);
        var originalPositions = FindProtectedPositions(originalDocument, text.Length);
        var candidatePositions = FindProtectedPositions(Markdown.Parse(candidate, DescriptionPipeline), candidate.Length);
        var sourceMap = MapSourcePositions(candidate.Length, inserted);

        // Restoring a tag must retain the same Markdown construct before and after neutralization.
        var removable = inserted
            .Where(position => MatchesProtection(originalPositions[position.SourceIndex], candidatePositions[position.RenderedIndex], sourceMap))
            .Select(position => position.RenderedIndex)
            .ToHashSet();
        return removable.Count == 0
            ? candidate
            : string.Concat(candidate.Where((_, index) => !removable.Contains(index)));
    }

    private static ProtectedRegion[] FindProtectedPositions(MarkdownDocument document, int length)
    {
        var positions = new ProtectedRegion[length];
        foreach (var node in document.Descendants())
        {
            if (node is LinkInline link)
            {
                MarkProtection(positions, ProtectedKind.LinkDestination, link.UrlSpan.Start, link.UrlSpan.End);
                continue;
            }

            if (node is CodeInline)
            {
                MarkProtection(positions, ProtectedKind.CodeInline, node.Span.Start, node.Span.End);
            }

            if (node is CodeBlock code)
            {
                // An unclosed fence's block span can cover only the opener; its lines retain body locations.
                var end = code.Span.End;
                for (var index = 0; index < code.Lines.Count; index++)
                {
                    end = Math.Max(end, code.Lines.Lines[index].Slice.End);
                }

                MarkProtection(positions, ProtectedKind.CodeBlock, code.Span.Start, end);
            }
        }

        return positions;
    }

    private static void MarkProtection(ProtectedRegion[] positions, ProtectedKind kind, int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(positions.Length - 1, end);
        if (end >= start)
        {
            Array.Fill(positions, new ProtectedRegion(kind, start, end), start, end - start + 1);
        }
    }

    private static int[] MapSourcePositions(int length, List<(int RenderedIndex, int SourceIndex)> inserted)
    {
        var positions = new int[length];
        var sourceIndex = 0;
        var insertionIndex = 0;
        for (var index = 0; index < length; index++)
        {
            if (insertionIndex < inserted.Count && inserted[insertionIndex].RenderedIndex == index)
            {
                positions[index] = inserted[insertionIndex++].SourceIndex;
            }
            else
            {
                positions[index] = sourceIndex++;
            }
        }

        return positions;
    }

    private static bool MatchesProtection(ProtectedRegion original, ProtectedRegion candidate, int[] sourceMap) =>
        original.Kind != ProtectedKind.None && original.Kind == candidate.Kind
                                            && original.Start == sourceMap[candidate.Start] && original.End == sourceMap[candidate.End];

    private static void MarkPositions(bool[] positions, int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(positions.Length - 1, end);
        if (end >= start)
        {
            Array.Fill(positions, true, start, end - start + 1);
        }
    }

    /// <summary>Neutralizes HTML in text embedded into headings, paths, signatures, actions or table cells.</summary>
    public static string RenderPlainText(string text) => NeutralizeHtml(text, null);

    private static string NeutralizeHtml(string text, bool[]? preserved, List<(int RenderedIndex, int SourceIndex)>? inserted = null)
    {
        var rendered = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            rendered.Append(text[index]);
            if (text[index] != '<' || index + 1 >= text.Length || (preserved is not null && preserved[index]))
            {
                continue;
            }

            var next = text[index + 1];
            if (char.IsLetter(next) || next is '!' or '/' or '?')
            {
                // Incomplete tags can continue across a newline into generated markup.
                inserted?.Add((rendered.Length, index));
                rendered.Append('\u200B');
            }
        }

        return rendered.ToString();
    }

    private static string RenderCell(string reason, Func<string, string> renderText)
    {
        var text = renderText(reason.ReplaceLineEndings(" "));
        var rendered = new StringBuilder(text.Length);
        var codePositions = FindCodeSpanPositions(text);
        var precedingBackslashes = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '|' && (codePositions[index] || precedingBackslashes % 2 == 0))
            {
                rendered.Append('\\');
            }

            rendered.Append(character);
            precedingBackslashes = character == '\\' ? precedingBackslashes + 1 : 0;
        }

        return rendered.ToString();
    }

    private static bool[] FindCodeSpanPositions(string text)
    {
        var positions = new bool[text.Length];
        for (var index = 0; index < text.Length;)
        {
            if (text[index] != '`' || IsEscaped(text, index))
            {
                index++;
                continue;
            }

            var delimiterLength = BacktickRunLength(text, index);
            var closing = FindClosingBackticks(text, index + delimiterLength, delimiterLength);
            if (closing < 0)
            {
                index += delimiterLength;
                continue;
            }

            Array.Fill(positions, true, index + delimiterLength, closing - index - delimiterLength);
            index = closing + delimiterLength;
        }

        return positions;
    }

    private static int FindClosingBackticks(string text, int start, int delimiterLength)
    {
        for (var index = start; index < text.Length;)
        {
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            var length = BacktickRunLength(text, index);
            if (length == delimiterLength)
            {
                return index;
            }

            index += length;
        }

        return -1;
    }

    /// <summary>Counts the backticks of the run that starts at <paramref name="start" />.</summary>
    internal static int BacktickRunLength(ReadOnlySpan<char> text, int start)
    {
        var end = start;
        while (end < text.Length && text[end] == '`')
        {
            end++;
        }

        return end - start;
    }

    private static bool IsEscaped(string text, int index)
    {
        var backslashes = 0;
        for (var preceding = index - 1; preceding >= 0 && text[preceding] == '\\'; preceding--)
        {
            backslashes++;
        }

        return backslashes % 2 != 0;
    }

    private static bool IsAssessmentTable(string assessment, out string[] levels)
    {
        levels = new string[4];
        var lines = assessment.Split('\n');
        if (lines.Length != 6 || lines[0] != TableHeader || lines[1] != TableSeparator)
        {
            return false;
        }

        var dimensions = new[] { "Risk", "Impact", "Likelihood", "Confidence" };
        for (var index = 0; index < dimensions.Length; index++)
        {
            var row = lines[index + 2];
            var prefix = $"| {dimensions[index]} | ";
            if (!row.StartsWith(prefix, StringComparison.Ordinal)
                || !row.EndsWith(" |", StringComparison.Ordinal) || CountCellDelimiters(row) != 4)
            {
                return false;
            }

            var levelEnd = row.IndexOf(" | ", prefix.Length, StringComparison.Ordinal);
            if (levelEnd < 0 || row[(levelEnd + 3)..^2].Trim().Length == 0)
            {
                return false;
            }

            var level = row[prefix.Length..levelEnd];
            levels[index] = level;
            var recognized = index switch
            {
                0 => IsLevel<FindingRiskSeverity>(level),
                1 => IsLevel<FindingImpact>(level),
                2 => level == "Not applicable" || IsLevel<FindingLikelihood>(level),
                _ => IsLevel<FindingConfidence>(level),
            };
            if (!recognized)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsLevel<T>(string value) where T : struct, Enum =>
        value == "Unassessed" || (Enum.TryParse<T>(value, out var level) && Enum.IsDefined(level) && FormatLevel(level) == value);

    private static int CountCellDelimiters(string row)
    {
        var delimiters = 0;
        for (var index = 0; index < row.Length; index++)
        {
            if (row[index] == '|' && (index == 0 || row[index - 1] != '\\'))
            {
                delimiters++;
            }
        }

        return delimiters;
    }

    private enum ProtectedKind
    {
        None,
        CodeInline,
        CodeBlock,
        LinkDestination,
    }

    private readonly record struct ProtectedRegion(ProtectedKind Kind, int Start, int End);
}
