// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Extracts search keywords for resolution memories stored before keyword extraction existed.
/// </summary>
/// <remarks>
///     <para>
///         Every row costs a model call, so this sweep is <strong>off unless an installation asks for it</strong>
///         and bounded when it is on. A backlog that quietly spends tokens on years of old memories is not a
///         backfill anybody asked for.
///     </para>
///     <para>
///         Deliberately its own port rather than a method on the shared memory repository: the memory boundary is
///         used by the review path, and widening it for a one-off catch-up owned by this slice would put a
///         code-insight concern in front of every reviewing caller.
///     </para>
/// </remarks>
public interface IThreadMemoryKeywordSweeper
{
    /// <summary>
    ///     Extracts and stores keywords for up to <paramref name="maxMemories" /> memories that have none, and
    ///     returns how many were enriched. Only memories belonging to clients whose collection gate is open are
    ///     considered.
    /// </summary>
    Task<int> SweepAsync(int maxMemories, CancellationToken ct = default);
}
