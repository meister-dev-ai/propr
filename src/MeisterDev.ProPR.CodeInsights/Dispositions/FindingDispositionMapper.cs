// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Dispositions;

/// <summary>
///     Maps a resolved thread's signals onto a disposition, as far as the signals alone can decide it.
/// </summary>
/// <remarks>
///     Deliberately pure and I/O-free: this is the part of the outcome that must be reproducible from stored
///     inputs, so it is separated from the model call that resolves the remaining case.
/// </remarks>
public static class FindingDispositionMapper
{
    /// <summary>
    ///     Returns the disposition the signals determine on their own, or <see langword="null" /> when the
    ///     finding was disregarded and only a judgement of the discussion can say whether it was wrong or
    ///     merely unwanted.
    /// </summary>
    /// <param name="intent">The provider-neutral meaning of the thread's close.</param>
    /// <param name="codeChange">Whether the anchored code changed since the finding was raised.</param>
    public static CodeInsightDisposition? MapFromSignals(
        ThreadResolutionIntent intent,
        ThreadAnchorCodeChange codeChange)
    {
        // A human explicitly accepting the concern is itself the outcome, and needs no code change to
        // corroborate it: "by design" and "won't fix" are agreement, not neglect.
        if (intent == ThreadResolutionIntent.AcceptedByHuman)
        {
            return CodeInsightDisposition.Acknowledged;
        }

        // A close that claims a fix is only "addressed" when a code change backs it up. Closing a thread
        // before, or without, changing anything is a claim, and treating a claim as a fix would inflate the
        // one number the whole feature exists to report honestly.
        if (intent == ThreadResolutionIntent.ClaimsFix && codeChange == ThreadAnchorCodeChange.Changed)
        {
            return CodeInsightDisposition.Addressed;
        }

        // Everything else was disregarded in effect: closed with no corroborating change, or closed without
        // any discernible resolution. Whether the finding was wrong or simply unwanted cannot be read from a
        // status, which is exactly why that split is judged from the discussion instead.
        return null;
    }
}
