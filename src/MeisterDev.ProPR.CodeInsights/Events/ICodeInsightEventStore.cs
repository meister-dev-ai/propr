// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Events;

/// <summary>
///     Persistence for the quality-condition event log.
/// </summary>
/// <remarks>
///     No publisher abstraction, no bus, and no handler interface: the table is the contract. The alerting
///     capability that will read it is shaped but not built, and inventing a delivery seam for a consumer that
///     does not exist would be machinery nobody can test.
/// </remarks>
public interface ICodeInsightEventStore
{
    /// <summary>
    ///     Returns the current state of one condition at one scope (the state its most recent transition left it
    ///     in) or <see langword="null" /> when it has never fired. This is what makes fire-once work without a
    ///     second bookkeeping table to keep in step.
    /// </summary>
    Task<CodeInsightConditionState?> GetCurrentStateAsync(
        CodeInsightEventScope scope,
        CodeInsightEventType eventType,
        CancellationToken ct = default);

    /// <summary>Appends one transition.</summary>
    Task AppendAsync(CodeInsightEvent transition, CancellationToken ct = default);

    /// <summary>
    ///     Returns a client's transitions at or after <paramref name="since" />, oldest first. The poll contract
    ///     for a future consumer, and how these tests read what was written.
    /// </summary>
    Task<IReadOnlyList<CodeInsightEvent>> GetByClientSinceAsync(
        Guid clientId,
        DateTimeOffset since,
        CancellationToken ct = default);
}
