// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Compares the latest revision of a pull request with the queued one for a provider that identifies a revision by
///     its head commit.
/// </summary>
/// <remarks>
///     The latest revision is read through the review query adapter of the provider. Both revisions are compared by
///     their stored revision key, which is the head commit for these providers. Commits have no order, so any head
///     other than the queued one counts as a later revision.
/// </remarks>
internal sealed class CommitLatestRevisionReader(ICodeReviewQueryService queryService) : ILatestRevisionReader
{
    public ScmProvider Provider => queryService.Provider;

    public async Task<bool> HasMovedPastAsync(ThreadPassJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var latest = await queryService.GetLatestRevisionAsync(job.ClientId, job.CodeReviewReference, ct);

        // Without a revision from the provider there is no evidence of a later revision, and the pass runs at the
        // revision it was queued at.
        var latestKey = ReviewRevisionKeys.TryGetStoredKey(latest);
        return latestKey is not null && !string.Equals(latestKey, job.RevisionKey, StringComparison.Ordinal);
    }
}
