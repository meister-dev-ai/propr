// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Translates the thread status vocabulary the review thread status writer receives into the two states a
///     provider that models a thread as resolved or unresolved is able to represent.
/// </summary>
internal static class ReviewThreadStatusVocabulary
{
    // The vocabulary is Azure DevOps' thread status names, because Azure DevOps was the only writer when the
    // interface was published and its callers still speak in those terms. Every name that closes a thread there
    // means resolved here, which is the same grouping the Azure DevOps read path applies when it decides a
    // thread no longer needs attention. A provider carrying only a boolean cannot tell "fixed" from "won't fix"
    // or "by design": resolving is the closest honest translation, and the distinction survives in the reply
    // text on the thread rather than in the flag.
    private static readonly HashSet<string> ResolvingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "fixed",
        "closed",
        "wontfix",
        "bydesign",
    };

    // Azure DevOps' "pending" means raised and awaiting the author, so like "active" it describes a thread that
    // is still open and maps to unresolved.
    private static readonly HashSet<string> ReopeningStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active",
        "pending",
    };

    /// <summary>Decides whether a status name asks for the thread to be resolved or reopened.</summary>
    /// <param name="status">The status name the caller supplied.</param>
    /// <param name="providerName">The provider named in the failure message.</param>
    /// <returns><c>true</c> to resolve the thread, <c>false</c> to reopen it.</returns>
    public static bool ResolvesThread(string status, string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var trimmed = status.Trim();
        if (ResolvingStatuses.Contains(trimmed))
        {
            return true;
        }

        if (ReopeningStatuses.Contains(trimmed))
        {
            return false;
        }

        // Azure DevOps' "unknown" clears a thread's status, which has no counterpart on a provider that knows
        // only resolved and unresolved. Either guess is wrong in a way nobody can see afterwards, closing a
        // thread nobody asked to close or reopening one that was settled, so the write is refused with the
        // accepted names spelled out instead.
        throw new InvalidOperationException(
            $"{providerName} review threads are either resolved or unresolved, and thread status '{trimmed}' has no equivalent. " +
            $"Use one of: {string.Join(", ", ResolvingStatuses.Concat(ReopeningStatuses).Order(StringComparer.Ordinal))}.");
    }
}
