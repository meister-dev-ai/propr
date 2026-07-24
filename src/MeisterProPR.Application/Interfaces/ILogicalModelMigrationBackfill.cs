// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     One-time, idempotent migration that moves a client's existing model configuration off concrete configured-model
///     ids onto named logical models. For each legacy review pass, and for each AI purpose still resolving through the
///     legacy per-connection binding, it synthesizes (or reuses) a per-client logical-model override that resolves to
///     the same connection + model + effort + protocol, then points the pass (or purpose) at that role by name. Safe to
///     run repeatedly — passes already carrying a logical-model name, and purposes already mapped, are skipped.
/// </summary>
public interface ILogicalModelMigrationBackfill
{
    /// <summary>Backfills one client's legacy review passes. Returns the number of passes migrated this run.</summary>
    Task<int> BackfillClientReviewPassesAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Backfills one client's unmapped AI purposes onto logical models (chat and embedding), mapping each purpose
    ///     that still has an active connection binding to a synthesized/reused override. Returns the number mapped this
    ///     run.
    /// </summary>
    Task<int> BackfillClientPurposesAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Backfills every client's legacy review passes and unmapped purposes. Returns the total number of passes and
    ///     purpose mappings migrated.
    /// </summary>
    Task<int> BackfillAllAsync(CancellationToken ct);
}
