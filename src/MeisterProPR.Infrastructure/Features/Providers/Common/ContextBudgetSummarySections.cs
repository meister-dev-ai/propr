// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Renders the plain-markdown "reviewed diff-only" and "skipped" sections that make context-window
///     budgeting decisions visible in a review summary, shared by the plain-markdown providers.
/// </summary>
public static class ContextBudgetSummarySections
{
    /// <summary>
    ///     Appends a section for files reviewed diff-only and a section for files skipped because they
    ///     exceeded the model context window, when the result contains any. No output when both are empty.
    /// </summary>
    /// <param name="builder">The summary builder to append to.</param>
    /// <param name="result">The review result carrying the degraded and skipped file paths.</param>
    public static void Append(StringBuilder builder, ReviewResult result)
    {
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
}
