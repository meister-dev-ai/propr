// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Services;

namespace MeisterDev.ProPR.CodeInsights.Survival;

/// <summary>One finding of an earlier increment, reduced to what a continuation check needs.</summary>
/// <param name="ChainId">The chain this finding already belongs to.</param>
/// <param name="FilePath">File the finding is anchored to, or <c>null</c> for a pull-request-level finding.</param>
/// <param name="Message">The finding text.</param>
public sealed record FindingChainCandidate(Guid ChainId, string? FilePath, string Message);

/// <summary>
///     Decides whether a finding in a new increment is the same problem an earlier increment already raised.
/// </summary>
/// <remarks>
///     <para>
///         Every increment materialises its own rows, so "the finding is still there" is not something the
///         records say by themselves: the same problem re-reported on a later revision is a different row. This
///         matcher links those rows into a chain, which is what makes the durable question answerable: of what a
///         review raised, how much was still being raised when the pull request finished, and how much simply
///         stopped being reported.
///     </para>
///     <para>
///         Same file, and wording similar enough to be one finding reported twice. The bar is the review
///         pipeline's own same-file duplicate threshold rather than a new number: two machine-written messages
///         about one problem in one file is exactly the case that threshold was chosen for, and inventing a
///         second notion of "the same finding" would let the two drift.
///     </para>
///     <para>
///         Deliberately <em>no</em> line-proximity rule, unlike the human-comment overlap check. Between
///         increments the code itself moves, so an anchor legitimately shifts by more than any window worth
///         setting; requiring proximity would report a persisting finding as vanished every time somebody
///         inserted a paragraph above it.
///     </para>
/// </remarks>
public static class FindingChainMatcher
{
    /// <summary>
    ///     Returns the chain the finding continues, or <see langword="null" /> when it is a new problem.
    /// </summary>
    /// <param name="filePath">File the new finding is anchored to.</param>
    /// <param name="message">The new finding's text.</param>
    /// <param name="earlier">
    ///     Findings from the pull request's previous increment, each with the chain it belongs to.
    /// </param>
    /// <param name="alreadyContinued">
    ///     Chains this increment has already continued. A chain continues at most once per increment: two
    ///     findings in one increment are two problems, even when they read alike, and letting both claim the same
    ///     predecessor would silently merge them.
    /// </param>
    public static Guid? FindContinuedChain(
        string? filePath,
        string message,
        IReadOnlyList<FindingChainCandidate> earlier,
        ISet<Guid> alreadyContinued)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(alreadyContinued);

        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        Guid? best = null;
        var bestSimilarity = 0.0;

        foreach (var candidate in earlier)
        {
            if (alreadyContinued.Contains(candidate.ChainId))
            {
                continue;
            }

            // A file-anchored finding is never the continuation of a pull-request-level one, or the reverse: a
            // summary remark would otherwise absorb whichever inline finding shared its vocabulary.
            if (!string.Equals(filePath, candidate.FilePath, StringComparison.Ordinal))
            {
                continue;
            }

            var similarity = FindingDeduplicator.JaccardSimilarity(message, candidate.Message);
            if (similarity >= FindingDeduplicator.SameFileDuplicateThreshold && similarity > bestSimilarity)
            {
                // The best match wins rather than the first: with several near-identical predecessors, taking
                // whichever happened to be enumerated first would attach the chain arbitrarily.
                best = candidate.ChainId;
                bestSimilarity = similarity;
            }
        }

        return best;
    }
}
