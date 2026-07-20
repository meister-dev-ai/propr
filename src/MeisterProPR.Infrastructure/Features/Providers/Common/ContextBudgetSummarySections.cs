// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Renders the plain-markdown budgeting-visibility sections that make context-window and USD-spend budgeting
///     decisions visible in a review summary, shared by the plain-markdown providers.
/// </summary>
public static class ContextBudgetSummarySections
{
    /// <summary>
    ///     Appends a note when the review was stopped early by the per-increment budget soft cap, a section for
    ///     files reviewed diff-only, and a section for files skipped because they exceeded the model context
    ///     window, when the result contains any. No output when none apply.
    /// </summary>
    /// <param name="builder">The summary builder to append to.</param>
    /// <param name="result">The review result carrying the budget and context-window outcomes.</param>
    public static void Append(StringBuilder builder, ReviewResult result)
    {
        if (result.BudgetSoftCapped)
        {
            builder.AppendLine();
            builder.AppendLine(FormatBudgetSoftCapNote(result));
            if (result.BudgetSoftCapSkippedFilePaths.Count > 0)
            {
                builder.AppendLine();
                foreach (var path in result.BudgetSoftCapSkippedFilePaths)
                {
                    builder.AppendLine($"- {path}");
                }
            }
        }

        if (result.ContextDegradedFilePaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(
                $"**Reviewed diff-only** ({result.ContextDegradedFilePaths.Count} files — too large for full context, reviewed from the diff alone)");
            builder.AppendLine();
            foreach (var path in result.ContextDegradedFilePaths)
            {
                builder.AppendLine($"- {path}");
            }
        }

        if (result.ContextSkippedFilePaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"**Skipped — exceeds model context window** ({result.ContextSkippedFilePaths.Count} files — not reviewed)");
            builder.AppendLine();
            foreach (var path in result.ContextSkippedFilePaths)
            {
                builder.AppendLine($"- {path}");
            }
        }
    }

    /// <summary>
    ///     Formats the one-line note stating that the review stopped early after reaching its per-increment budget
    ///     soft cap, including the threshold, the metered spend, and how many files were consequently not scanned.
    /// </summary>
    public static string FormatBudgetSoftCapNote(ReviewResult result)
    {
        var threshold = result.BudgetSoftCapThresholdUsd?.ToString("0.00", CultureInfo.InvariantCulture);
        var spent = result.BudgetSoftCapSpentUsd?.ToString("0.00", CultureInfo.InvariantCulture);
        var skipped = result.BudgetSoftCapSkippedFilePaths.Count;
        var fileWord = skipped == 1 ? "file" : "files";
        return $"**Budget soft cap reached** — this review stopped scanning further files after reaching its "
               + $"per-review budget soft cap (${threshold} cap; ${spent} spent). {skipped} {fileWord} not reviewed.";
    }
}
