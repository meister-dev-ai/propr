// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Decodes the stable public mutation vocabulary into a neutral resolved/unresolved command.</summary>
internal static class ReviewThreadStatusVocabulary
{
    // These public command names are retained independently of native read-status grammars.
    private static readonly HashSet<string> ResolvingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "fixed",
        "closed",
        "wontfix",
        "bydesign",
    };

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

        // Refuse values outside the public command vocabulary before resolving live credentials.
        throw new InvalidOperationException(
            $"{providerName} review threads are either resolved or unresolved, and thread status '{trimmed}' has no equivalent. " +
            $"Use one of: {string.Join(", ", ResolvingStatuses.Concat(ReopeningStatuses).Order(StringComparer.Ordinal))}.");
    }
}
