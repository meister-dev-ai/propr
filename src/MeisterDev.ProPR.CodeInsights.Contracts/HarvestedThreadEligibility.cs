// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

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
    ///     The prefix ProPR's own pull-request summary starts with on Azure DevOps. Matched rather than guessed:
    ///     this is the literal the Azure DevOps poster writes and the same literal it checks to avoid posting
    ///     twice.
    /// </summary>
    /// <remarks>
    ///     Azure DevOps only. The other providers publish their summary through a review body that opens with the
    ///     reviewer's own display name ("## {name} Review"), which is per-installation and so cannot be matched by
    ///     a constant. On those providers this check therefore does nothing, and keeping ProPR's summary out of the
    ///     miss harvest rests entirely on the identity checks that run first: recorded provenance, and the posting
    ///     identity learned from it. That is enough for any installation that recorded provenance, which is every
    ///     installation collecting insights today. It is not enough for a summary posted before provenance
    ///     existed on a non-Azure-DevOps provider: such a thread is harvested as a human thread ProPR missed.
    ///     Closing that gap means giving every publisher a shared, matchable prefix, which changes what reviewers
    ///     see on the pull request and is therefore a product decision rather than a fix.
    ///     <para>
    ///         The literal stays English whatever output language a client configured. It is matched as data against
    ///         summaries already stored on pull requests, so translating it would make ProPR's own summaries stop
    ///         matching: they would be harvested as human threads ProPR missed, and the poster would post a second
    ///         summary alongside the first. The client's language governs the model's prose, not this label.
    ///     </para>
    /// </remarks>
    public const string SummaryPrefix = "**AI Review Summary**";

    /// <summary>
    ///     Author identities that belong to a provider rather than to a person, matched against the
    ///     <c>author</c> half of each stored line.
    /// </summary>
    /// <remarks>
    ///     The one entry is Azure DevOps' own service identity, the fixed account it attributes activity entries to:
    ///     "added a reviewer", a vote, a policy result. Those come back through the same comments API as replies, so
    ///     a thread made only of them would otherwise read as a human thread ProPR failed to raise. The literal is a
    ///     well-known constant of the provider, not a per-installation value, which is why it can be matched at all.
    ///     Matching happens on the identity because that is what the crawl stores for it: the author line holds the
    ///     account id whenever the provider supplies one and falls back to a display name only when it does not.
    ///     <para>
    ///         Adding a provider here is not as simple as adding its bot's name. Azure DevOps supplies a real Guid,
    ///         which is why its literal appears above verbatim. The other providers have string user ids, which the
    ///         crawl converts to a deterministic Guid before storing, so an entry for them is
    ///         <c>StableGuidGenerator.Create("&lt;their user id&gt;")</c> and not a name. Whether that is worth doing
    ///         depends on the provider: only Azure DevOps returns its activity entries through the comments API in
    ///         the first place.
    ///     </para>
    /// </remarks>
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
