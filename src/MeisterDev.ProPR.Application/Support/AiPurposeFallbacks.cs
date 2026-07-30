// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Support;

/// <summary>
///     Which purpose a purpose degrades to when it has no model of its own.
/// </summary>
/// <remarks>
///     One definition, because the chain is consulted from more than one place: the connection-binding lookup and
///     the logical-model role lookup. Two copies would drift, and the failure mode of a drifted chain is a purpose
///     that silently runs on a different model than an operator was told.
/// </remarks>
public static class AiPurposeFallbacks
{
    /// <summary>
    ///     Returns the purpose to try next, or <see langword="null" /> when this purpose is the end of its chain.
    /// </summary>
    public static AiPurpose? Next(AiPurpose purpose)
    {
        return purpose switch
        {
            // Cheap per-file triage falls back to the low-effort review model (which itself falls back to
            // ReviewDefault below), so the model still judges complexity on a cheap model when no dedicated
            // triage binding is configured: instead of silently dropping to the size heuristic.
            AiPurpose.ReviewTriage => AiPurpose.ReviewLowEffort,

            // Evidence-gathering verification falls back to the cheap triage model (then to low-effort →
            // default), so verification runs on an independent, inexpensive model rather than self-verifying
            // on the reviewer's model when no dedicated verification binding is configured.
            AiPurpose.ReviewVerification => AiPurpose.ReviewTriage,

            // Classifying a finding for analytics is a small, cheap prompt over prose, so it degrades to the same
            // cheap model triage uses. Without this an installation that switched Code Insights on would collect
            // findings and never classify any of them: the purpose is new, so no existing installation has it
            // bound, and an unbound purpose resolves to nothing.
            AiPurpose.InsightsClassification => AiPurpose.ReviewTriage,

            AiPurpose.ProRVPrefilter
                or AiPurpose.ReviewLowEffort
                or AiPurpose.ReviewMediumEffort
                or AiPurpose.ReviewHighEffort => AiPurpose.ReviewDefault,

            _ => null,
        };
    }

    /// <summary>
    ///     Enumerates <paramref name="purpose" />'s fallbacks in order, excluding the purpose itself. Bounded by
    ///     the chain's own length, and defensive against a cycle a future edit could introduce.
    /// </summary>
    public static IEnumerable<AiPurpose> Chain(AiPurpose purpose)
    {
        var seen = new HashSet<AiPurpose> { purpose };
        var current = Next(purpose);

        while (current is not null && seen.Add(current.Value))
        {
            yield return current.Value;
            current = Next(current.Value);
        }
    }
}
