// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;

namespace MeisterDev.ProPR.Application.ValueObjects;

/// <summary>
///     Turns a client's ordered review-pass list into stable keys recorded alongside each per-file result.
///     Completion on its own says a file was reviewed, not what it was reviewed by, so without these a resume
///     would adopt work produced under a pass list the client has since changed.
/// </summary>
public static class ReviewPassSignature
{
    /// <summary>
    ///     The keys for an ordered pass list. Position is part of the key because the list is ordered and a
    ///     pass's ordinal is how its trace is identified.
    /// </summary>
    /// <param name="passes">The client's configured review passes.</param>
    public static IReadOnlyList<string> ForPasses(IReadOnlyList<ReviewPassSpec>? passes)
    {
        if (passes is null || passes.Count == 0)
        {
            return [];
        }

        var keys = new List<string>(passes.Count);
        for (var index = 0; index < passes.Count; index++)
        {
            keys.Add(KeyFor(index + 1, passes[index]));
        }

        return keys;
    }

    /// <summary>
    ///     Whether a result recorded under <paramref name="recorded" /> still matches
    ///     <paramref name="current" />, and may therefore be adopted rather than reviewed again.
    /// </summary>
    /// <remarks>
    ///     An empty recorded set is taken at face value. Results written before pass provenance existed, and
    ///     results carried forward from another revision, both have none, and re-reviewing every one of them
    ///     would charge an installation for its whole backlog on the first upgrade.
    /// </remarks>
    public static bool Matches(IReadOnlyList<string>? recorded, IReadOnlyList<string> current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (recorded is null || recorded.Count == 0)
        {
            return true;
        }

        return recorded.SequenceEqual(current, StringComparer.Ordinal);
    }

    private static string KeyFor(int ordinal, ReviewPassSpec pass)
    {
        // The model identity is whichever one actually selects the model: a named logical model when the pass
        // has one, and the configured model id for a pass that has not been migrated to one.
        var model = string.IsNullOrWhiteSpace(pass.LogicalModelName)
            ? pass.ConfiguredModelId.ToString("D")
            : pass.LogicalModelName;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ordinal}:{model}:{pass.Lens ?? "-"}:{pass.Scope ?? "-"}:{pass.ReasoningEffort}");
    }
}
