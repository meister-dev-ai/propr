// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal sealed class ReviewerPerformanceRepositoryMatcher
{
    private readonly Dictionary<string, ReviewerPerformanceIdentity.RepositoryFallback?> _decodedSelectors = new(StringComparer.Ordinal);

    internal Selection Prepare(IReadOnlyList<string>? selectors)
    {
        if (selectors is null)
        {
            return new(null, []);
        }

        var exactIdentities = new HashSet<string>(selectors, StringComparer.Ordinal);
        var unknownScopeRepositories = new HashSet<ReviewerPerformanceIdentity.RepositoryFallback>();
        foreach (var identity in exactIdentities)
        {
            if (!this._decodedSelectors.TryGetValue(identity, out var fallback))
            {
                fallback = ReviewerPerformanceIdentity.DecodeRepositoryFallback(identity);
                // Cache invalid results as well; this owner lasts for one view and stores only selection/axis identities.
                this._decodedSelectors.Add(identity, fallback);
            }

            if (fallback is { } repository)
            {
                unknownScopeRepositories.Add(repository);
            }
        }

        return new(exactIdentities, unknownScopeRepositories);
    }

    internal enum MatchKind
    {
        None,
        Exact,
        UnknownScope
    }

    internal sealed class Selection(
        HashSet<string>? exactIdentities,
        HashSet<ReviewerPerformanceIdentity.RepositoryFallback> unknownScopeRepositories)
    {
        internal MatchKind Match(ReviewerPerformanceDailyCount row)
        {
            if (exactIdentities is null)
            {
                return MatchKind.Exact;
            }

            if (exactIdentities.Count == 0)
            {
                return MatchKind.None;
            }

            // Encode once for the whole selection, preserving byte-for-byte canonical identity matching.
            if (exactIdentities.Contains(ReviewerPerformanceIdentity.RepositoryKey(row)))
            {
                return MatchKind.Exact;
            }

            if (row.ProviderScope.Length == 0 && unknownScopeRepositories.Count != 0
                                              && unknownScopeRepositories.Contains(new(row.ClientId.ToString(), row.RepositoryId)))
            {
                return MatchKind.UnknownScope;
            }

            return MatchKind.None;
        }

        internal bool Matches(ReviewerPerformanceDailyCount row, bool includeUnknownScope)
        {
            var match = this.Match(row);
            return match == MatchKind.Exact || (includeUnknownScope && match == MatchKind.UnknownScope);
        }
    }
}
