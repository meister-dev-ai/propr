// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Support;

/// <summary>Builds a stable persisted revision key for review-scan comparisons across providers.</summary>
public static class ReviewRevisionKeys
{
    /// <summary>Gets the stored key for a review revision, falling back to iteration ID if unavailable.</summary>
    /// <param name="revision">The review revision.</param>
    /// <param name="fallbackIterationId">The fallback iteration ID to use if no stored key is available.</param>
    /// <returns>The stored key or the fallback iteration ID as a string.</returns>
    public static string GetStoredKey(ReviewRevision? revision, int fallbackIterationId)
    {
        return TryGetStoredKey(revision)
               ?? fallbackIterationId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Attempts to get the stored key for a review revision.</summary>
    /// <param name="revision">The review revision.</param>
    /// <returns>The stored key if available; otherwise null.</returns>
    public static string? TryGetStoredKey(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(revision.ProviderRevisionId))
        {
            return revision.ProviderRevisionId;
        }

        if (!string.IsNullOrWhiteSpace(revision.PatchIdentity))
        {
            return revision.PatchIdentity;
        }

        return string.Concat(
            revision.BaseSha,
            "::",
            revision.HeadSha,
            "::",
            revision.StartSha ?? string.Empty);
    }

    /// <summary>Attempts to parse a stored key as an iteration ID.</summary>
    /// <param name="storedKey">The stored key to parse.</param>
    /// <returns>The parsed iteration ID if valid and greater than 0; otherwise null.</returns>
    public static int? TryParseIterationId(string? storedKey)
    {
        return int.TryParse(storedKey, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIterationId)
               && parsedIterationId > 0
            ? parsedIterationId
            : null;
    }
}
