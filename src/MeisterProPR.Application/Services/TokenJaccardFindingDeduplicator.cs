// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Default <see cref="IFindingDeduplicator" />: the existing token-set Jaccard behavior. Same-file
///     near-duplicates are collapsed by <see cref="FindingDeduplicator.CollapseSameFileDuplicates" /> and
///     cross-file root-cause duplicates are consolidated by <see cref="FindingDeduplicator.Deduplicate" />, in
///     exactly the order the synthesis inlet has always applied them.
/// </summary>
public sealed class TokenJaccardFindingDeduplicator : IFindingDeduplicator
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ReviewComment>> DeduplicateAsync(
        IReadOnlyList<ReviewComment> comments,
        Guid clientId,
        CancellationToken ct = default)
    {
        _ = clientId;
        var deduped = FindingDeduplicator.Deduplicate(FindingDeduplicator.CollapseSameFileDuplicates(comments));
        return Task.FromResult(deduped);
    }
}
