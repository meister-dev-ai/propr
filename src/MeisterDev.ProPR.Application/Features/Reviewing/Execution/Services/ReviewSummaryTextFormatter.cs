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
    private const int MaximumCompactActions = 20;
    private const int MaximumCompactActionCharacters = 8000;
    private const int MaximumMessageScalars = 320;
    private const int MaximumLocationScalars = 160;

    /// <summary>Formats finding actions and offered-finding totals without changing the review result.</summary>
    /// <param name="result">The findings offered to the provider after publication policy evaluation.</param>
    /// <param name="includeFilelessFindingDetails">Whether findings without an inline anchor need their complete text in the summary.</param>
    /// <param name="findingRenderer">The provider's renderer for complete findings without an inline anchor.</param>
    /// <param name="renderText">The provider's safe rendering of free text and file paths.</param>
    public static string Format(
        ReviewResult result,
        bool includeFilelessFindingDetails = false,
        Func<ReviewComment, string>? findingRenderer = null,
        Func<string, string>? renderText = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var providerTextRenderer = renderText;
        findingRenderer ??= comment => ReviewFindingTextFormatter.Format(comment, renderText: providerTextRenderer);
        renderText ??= ReviewFindingTextFormatter.RenderPlainText;
        var entries = new List<string>();
        var compactActions = 0;
        var compactActionCharacters = 0;
        var omittedActions = 0;
        if (result.Comments.Count > 0)
        {
            foreach (var comment in ReviewFindingPriority.Ordered(result))
            {
                if (includeFilelessFindingDetails && (string.IsNullOrWhiteSpace(comment.FilePath) || comment.LineNumber is not > 0))
                {
                    var location = string.IsNullOrWhiteSpace(comment.FilePath) ? null : FormatLocation(comment, renderText);
                    entries.Add(
                        ReviewPresentationTemplateCatalog.Shared.Render(
                            ReviewPresentationTemplate.SummaryDetails, new ReviewPresentationModels.SummaryDetails(location, findingRenderer(comment))));
                }
                else
                {
                    if (compactActions >= MaximumCompactActions)
                    {
                        omittedActions++;
                        continue;
                    }

                    var classification = comment.Severity switch
                    {
                        CommentSeverity.Error => "Error",
                        CommentSeverity.Warning => "Warning",
                        CommentSeverity.Suggestion => "Suggestion",
                        _ => "Info",
                    };
                    var message = renderText(Excerpt(comment.Message.ReplaceLineEndings(" "), MaximumMessageScalars));
                    var risk = comment.RiskAssessment is { } assessment ? ReviewFindingTextFormatter.FormatLevel(assessment.Severity) : null;
                    var action = ReviewPresentationTemplateCatalog.Shared.Render(
                        ReviewPresentationTemplate.SummaryAction,
                        new ReviewPresentationModels.SummaryAction(classification, FormatLocation(comment, renderText, MaximumLocationScalars), message, risk));

                    var actionCharacters = action.Length + 2 * Environment.NewLine.Length;
                    if (compactActionCharacters + actionCharacters > MaximumCompactActionCharacters)
                    {
                        omittedActions++;
                        continue;
                    }

                    entries.Add(action);
                    compactActions++;
                    compactActionCharacters += actionCharacters;
                }
            }
        }

        var riskDistribution = FormatDistribution(result.Comments.Select(comment => comment.RiskAssessment?.Severity ?? FindingRiskSeverity.Unknown));
        var confidence = FormatDistribution(result.Comments.Select(comment => comment.RiskAssessment?.Confidence ?? FindingConfidence.Unknown));
        return ReviewPresentationTemplateCatalog.Shared.Render(
            ReviewPresentationTemplate.Summary,
            new ReviewPresentationModels.Summary(result.Comments.Count > 0, entries, omittedActions, result.Comments.Count, riskDistribution, confidence));
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
