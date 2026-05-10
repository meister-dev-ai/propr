// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Rebuilds selected ProCursor token usage rollup intervals from retained raw events.
/// </summary>
public interface IProCursorTokenUsageRebuildService
{
    /// <summary>
    ///     Rebuilds selected ProCursor token usage rollup intervals from retained raw events.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="request">The rebuild request.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The rebuild response.</returns>
    Task<ProCursorTokenUsageRebuildResponse> RebuildAsync(
        Guid clientId,
        ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default);
}
