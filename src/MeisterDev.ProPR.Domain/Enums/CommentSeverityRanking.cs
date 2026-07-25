// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Product severity ranking for review comments, used to compare a finding's severity against a configured
///     minimum-severity-to-post threshold. A higher rank is more severe. This ordering is deliberate and independent
///     of the <see cref="CommentSeverity" /> enum's declaration order (which is not a severity order).
/// </summary>
public static class CommentSeverityRanking
{
    /// <summary>
    ///     Returns the product severity rank: <see cref="CommentSeverity.Error" /> (3) &gt;
    ///     <see cref="CommentSeverity.Warning" /> (2) &gt; <see cref="CommentSeverity.Suggestion" /> (1) &gt;
    ///     <see cref="CommentSeverity.Info" /> (0).
    /// </summary>
    /// <param name="severity">The comment severity to rank.</param>
    public static int Rank(this CommentSeverity severity)
    {
        return severity switch
        {
            CommentSeverity.Error => 3,
            CommentSeverity.Warning => 2,
            CommentSeverity.Suggestion => 1,
            CommentSeverity.Info => 0,
            _ => 0,
        };
    }

    /// <summary>
    ///     Returns whether a comment of the given <paramref name="severity" /> meets or exceeds the
    ///     <paramref name="minimum" /> threshold, and should therefore be posted to the SCM provider.
    /// </summary>
    /// <param name="severity">The severity of the finding under consideration.</param>
    /// <param name="minimum">The configured minimum severity to post.</param>
    public static bool MeetsMinimum(this CommentSeverity severity, CommentSeverity minimum)
    {
        return severity.Rank() >= minimum.Rank();
    }
}
