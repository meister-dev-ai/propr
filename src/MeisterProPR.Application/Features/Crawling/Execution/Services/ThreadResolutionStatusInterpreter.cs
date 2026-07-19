// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Crawling.Execution.Services;

/// <summary>
///     Maps a provider's native reviewer-thread status onto a provider-neutral
///     <see cref="ThreadResolutionIntent" />, so the thread-memory state machine never branches on
///     provider-specific status strings. Shared by every crawl entry point that reconciles thread memory.
/// </summary>
internal static class ThreadResolutionStatusInterpreter
{
    /// <summary>
    ///     Classifies a native thread status. "Fixed"/"Closed" only claim the concern was addressed;
    ///     "WontFix"/"ByDesign" are deliberate human acceptances; anything else (including "Active",
    ///     "Pending", null) is still open.
    /// </summary>
    public static ThreadResolutionIntent InterpretIntent(string? status)
    {
        if (string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase))
        {
            return ThreadResolutionIntent.AcceptedByHuman;
        }

        if (string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase))
        {
            return ThreadResolutionIntent.ClaimsFix;
        }

        return ThreadResolutionIntent.Active;
    }

    /// <summary>True when the intent represents any resolved state (accepted or claimed-fixed).</summary>
    public static bool IsResolved(ThreadResolutionIntent intent) => intent != ThreadResolutionIntent.Active;
}
