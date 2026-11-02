// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Formats recorded findings for a provider-neutral review summary.</summary>
public static class ReviewSummaryTextFormatter
{
    /// <summary>
    ///     Formats the findings that have no comment of their own and the offered-finding totals, without changing the
    ///     review result. A finding with an inline comment is left out, because its comment already carries it.
    /// </summary>
    /// <param name="result">The findings offered to the provider after publication policy evaluation.</param>
    /// <param name="includeFilelessFindingDetails">
    ///     Whether findings without an inline comment need their complete text in the summary. A provider that posts
    ///     every finding as a thread of its own passes <see langword="false" />, and the summary then lists no finding.
    /// </param>
    /// <param name="findingRenderer">The provider's renderer for complete findings without an inline comment.</param>
    /// <param name="renderText">The provider's safe rendering of free text and file paths.</param>
    /// <param name="hasInlineComment">
    ///     Whether the provider publishes a finding as an inline comment. Defaults to every finding with a file and
    ///     a line. A provider that cannot anchor some of those findings passes its own rule, so that the summary
    ///     carries their complete text.
    /// </param>
    /// <param name="findingsNote">A sentence placed after the listed findings, or <see langword="null" /> for none.</param>
    public static string Format(
        ReviewResult result,
        bool includeFilelessFindingDetails = false,
        Func<ReviewComment, string>? findingRenderer = null,
        Func<string, string>? renderText = null,
        Func<ReviewComment, bool>? hasInlineComment = null,
        string? findingsNote = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        hasInlineComment ??= comment => !string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber is > 0;
        var providerTextRenderer = renderText;
        findingRenderer ??= comment => ReviewFindingTextFormatter.Format(comment, renderText: providerTextRenderer);
        renderText ??= ReviewFindingTextFormatter.RenderPlainText;
        var entries = new List<string>();
        if (includeFilelessFindingDetails)
        {
            foreach (var comment in ReviewFindingPriority.Ordered(result).Where(comment => !hasInlineComment(comment)))
            {
                var location = string.IsNullOrWhiteSpace(comment.FilePath) ? null : FormatLocation(comment, renderText);
                entries.Add(
                    ReviewPresentationTemplateCatalog.Shared.Render(
                        ReviewPresentationTemplate.SummaryDetails, new ReviewPresentationModels.SummaryDetails(location, findingRenderer(comment))));
            }
        }

        var riskDistribution = FormatDistribution(result.Comments.Select(comment => comment.RiskAssessment?.Severity ?? FindingRiskSeverity.Unknown));
        var confidence = FormatDistribution(result.Comments.Select(comment => comment.RiskAssessment?.Confidence ?? FindingConfidence.Unknown));
        return ReviewPresentationTemplateCatalog.Shared.Render(
            ReviewPresentationTemplate.Summary,
            new ReviewPresentationModels.Summary(entries, entries.Count > 0 ? findingsNote : null, result.Comments.Count, riskDistribution, confidence));
    }

    private static string Excerpt(string text, int maximumScalars)
    {
        var scalars = 0;
        var characters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (scalars == maximumScalars)
            {
                return text[..characters] + "…";
            }

            scalars++;
            characters += rune.Utf16SequenceLength;
        }

        return text;
    }

    private static string FormatDistribution<T>(IEnumerable<T> levels) where T : struct, Enum
    {
        var counts = levels.GroupBy(level => level).OrderByDescending(group => group.Key)
            .Select(group => $"{ReviewFindingTextFormatter.FormatLevel(group.Key)}: {group.Count().ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        return counts.Length == 0 ? "None" : string.Join(", ", counts);
    }

    internal static string FormatLocation(ReviewComment comment, Func<string, string> renderText, int? maximumPathScalars = null)
    {
        if (string.IsNullOrWhiteSpace(comment.FilePath))
        {
            return "PR-level";
        }

        var path = comment.FilePath.ReplaceLineEndings(" ");
        if (maximumPathScalars.HasValue)
        {
            path = Excerpt(path, maximumPathScalars.Value);
        }

        path = renderText(path);
        var location = comment.LineNumber is > 0 ? path + ":" + comment.LineNumber.Value.ToString(CultureInfo.InvariantCulture) : path;
        var maximumBackticks = 0;
        var consecutiveBackticks = 0;
        foreach (var character in location)
        {
            consecutiveBackticks = character == '`' ? consecutiveBackticks + 1 : 0;
            maximumBackticks = Math.Max(maximumBackticks, consecutiveBackticks);
        }

        var delimiter = new string('`', maximumBackticks + 1);
        var padding = location.StartsWith('`') || location.EndsWith('`') ? " " : string.Empty;
        return delimiter + padding + location + padding + delimiter;
    }
}
