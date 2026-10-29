// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Renders the plain-markdown budgeting-visibility sections that make context-window and USD-spend budgeting
///     decisions visible in a review summary, shared by the plain-markdown providers.
/// </summary>
public static class ContextBudgetSummarySections
{
    /// <summary>
    ///     Appends a note when the review was stopped early by the per-increment budget soft cap or by the maximum
    ///     review duration of its tenant, a section for files reviewed diff-only, and a section for files skipped
    ///     because they exceeded the model context window, when the result contains any. No output when none
    ///     apply.
    /// </summary>
    /// <param name="builder">The summary builder to append to.</param>
    /// <param name="result">The review result carrying the budget, time-limit and context-window outcomes.</param>
    /// <param name="renderText">The provider's safe rendering of file paths.</param>
    public static void Append(StringBuilder builder, ReviewResult result, Func<string, string>? renderText = null)
    {
        renderText ??= ReviewFindingTextFormatter.RenderPlainText;
        if (result.BudgetSoftCapped)
        {
            builder.AppendLine();
            builder.AppendLine(FormatBudgetSoftCapNote(result));
            if (result.BudgetSoftCapSkippedFilePaths.Count > 0)
            {
                builder.AppendLine();
                foreach (var path in result.BudgetSoftCapSkippedFilePaths)
                {
                    builder.AppendLine($"- {renderText(path.ReplaceLineEndings(" "))}");
                }
            }
        }

        if (result.TimeLimitReached)
        {
            builder.AppendLine();
            builder.AppendLine(FormatTimeLimitNote(result));
            if (result.TimeLimitSkippedFilePaths.Count > 0)
            {
                builder.AppendLine();
                foreach (var path in result.TimeLimitSkippedFilePaths)
                {
                    builder.AppendLine($"- {renderText(path.ReplaceLineEndings(" "))}");
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
                builder.AppendLine($"- {renderText(path.ReplaceLineEndings(" "))}");
            }
        }

        if (result.ContextSkippedFilePaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"**Skipped — exceeds model context window** ({result.ContextSkippedFilePaths.Count} files — not reviewed)");
            builder.AppendLine();
            foreach (var path in result.ContextSkippedFilePaths)
            {
                builder.AppendLine($"- {renderText(path.ReplaceLineEndings(" "))}");
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

    /// <summary>
    ///     Formats the note for a review that ended at the maximum review duration of its tenant, with the duration,
    ///     how many files were not reviewed, and whether the pull-request-wide passes did not run. The paths of the
    ///     files follow the note as a list.
    /// </summary>
    public static string FormatTimeLimitNote(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var limit = result.TimeLimitSeconds is { } seconds
            ? $"the tenant's time limit of {FormatDuration(seconds)}"
            : "the tenant's time limit";
        var skipped = result.TimeLimitSkippedFilePaths.Count;
        var files = skipped switch
        {
            0 => "Every file was reviewed.",
            1 => "1 file was not reviewed.",
            _ => string.Create(CultureInfo.InvariantCulture, $"{skipped} files were not reviewed."),
        };
        var passes = result.TimeLimitSkippedPrWidePasses ? " The pull-request-wide passes did not run." : string.Empty;
        return $"**Time limit reached.** The review ended at {limit}. {files}{passes}";
    }

    /// <summary>Writes a duration in whole hours or minutes where it divides evenly, and in seconds otherwise.</summary>
    private static string FormatDuration(int seconds)
    {
        static string Count(int value, string unit) =>
            string.Create(CultureInfo.InvariantCulture, $"{value} {unit}{(value == 1 ? string.Empty : "s")}");

        if (seconds % 3600 == 0)
        {
            return Count(seconds / 3600, "hour");
        }

        return seconds % 60 == 0 ? Count(seconds / 60, "minute") : Count(seconds, "second");
    }
}
