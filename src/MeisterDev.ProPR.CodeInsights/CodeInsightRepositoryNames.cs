// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights;

/// <summary>
///     Resolves repository display names for a set of clients, so a ranked list can name a repository instead of
///     printing the provider's identifier at a reader.
/// </summary>
/// <remarks>
///     <para>
///         Every read here counts and groups by <c>RepositoryId</c>, because that is the identity the collected
///         facts carry. For several providers it is a bare number, which on screen reads as anything but a
///         repository, so the name is looked up separately and joined in memory rather than being denormalised
///         into the projection, where a rename would leave stale copies behind in every historical cell.
///     </para>
///     <para>
///         A repository with no recorded name is absent from the map rather than present as an empty string:
///         the caller then falls back to the identifier, which is always true if not always friendly.
///     </para>
/// </remarks>
internal static class CodeInsightRepositoryNames
{
    /// <summary>
    ///     Loads the recorded display name per (client, repository) for the given clients. The client filter is
    ///     unconditional, like every other code-insight read.
    /// </summary>
    public static async Task<Dictionary<(Guid ClientId, string RepositoryId), string>> LoadAsync(
        MeisterProPRDbContext db,
        IReadOnlyCollection<Guid> clientIds,
        CancellationToken ct)
    {
        if (clientIds.Count == 0)
        {
            return [];
        }

        var ids = clientIds.ToList();

        var rows = await db.CodeInsightPullRequests
            .AsNoTracking()
            .Where(pullRequest => ids.Contains(pullRequest.ClientId) && pullRequest.RepositoryName != null)
            .Select(pullRequest => new
            {
                pullRequest.ClientId,
                pullRequest.RepositoryId,
                pullRequest.RepositoryName,
                pullRequest.UpdatedAt,
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => (row.ClientId, row.RepositoryId))
            // The most recently touched aggregate wins, so a renamed repository settles on its current name
            // rather than on whichever row the database happened to return first.
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(row => row.UpdatedAt).First().RepositoryName!);
    }
}
