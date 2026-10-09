// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Normalizes optional author display metadata without exposing account email addresses.</summary>
internal static class ReviewOverviewPresentation
{
    public static string? AuthorName(string? displayName, string? login)
    {
        foreach (var candidate in new[] { displayName, login })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Contains('@'))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
