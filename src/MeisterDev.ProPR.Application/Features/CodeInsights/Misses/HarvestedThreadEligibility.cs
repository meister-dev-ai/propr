// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Misses;

/// <summary>
///     Whether a harvested thread is something a person actually said, judged from the discussion text alone.
/// </summary>
/// <remarks>
///     <para>
///         The load-bearing checks live earlier and use identities rather than text: the crawl marks ProPR's own
///         comments from recorded provenance, and the provider adapters mark their own activity entries. This is
///         the fallback for the cases those cannot reach, and there are two.
///     </para>
///     <para>
///         First, an installation whose summaries were posted before provenance was recorded has nothing
///         identifying them, so its own summary comes back looking like a human thread. Second, rows harvested
///         under older rules are already stored, and no later fix re-judges them; they are read back as they were
///         written. Both are answerable from the text ProPR itself produced, which is why this is text-based
///         where nothing else here is.
///     </para>
/// </remarks>
public static class HarvestedThreadEligibility
{
    /// <summary>
    ///     The prefix ProPR's own pull-request summary starts with. Matched rather than guessed: this is the
    ///     literal the Azure DevOps poster writes and the same literal it checks to avoid posting twice.
    /// </summary>
    public const string SummaryPrefix = "**AI Review Summary**";

    /// <summary>
    ///     Provider identities that author activity entries rather than comments. Azure DevOps uses one fixed
    ///     identity for "added a reviewer", votes, and policy results, and returns them through the same comments
    ///     API as replies.
    /// </summary>
    private static readonly string[] ProviderActivityIdentities =
    [
        "00000002-0000-8888-8000-000000000000",
    ];

    /// <summary>
    ///     Whether the discussion is a human thread worth measuring recall against. False for ProPR's own summary
    ///     and for a thread made only of provider activity.
    /// </summary>
    /// <param name="discussion">
    ///     The stored discussion, one <c>author: text</c> line per comment as the harvester builds it.
    /// </param>
    public static bool IsHumanThread(string? discussion)
    {
        if (string.IsNullOrWhiteSpace(discussion))
        {
            return false;
        }

        var lines = discussion.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return false;
        }

        var sawHumanLine = false;
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var author = separator < 0 ? string.Empty : line[..separator].Trim();
            var text = separator < 0 ? line : line[(separator + 1)..].Trim();

            if (text.StartsWith(SummaryPrefix, StringComparison.Ordinal))
            {
                // ProPR's own summary. Whatever else the thread holds, this is not a thread it failed to raise.
                return false;
            }

            if (!ProviderActivityIdentities.Contains(author, StringComparer.OrdinalIgnoreCase))
            {
                sawHumanLine = true;
            }
        }

        // A thread of nothing but activity entries has nobody to have said anything.
        return sawHumanLine;
    }
}
