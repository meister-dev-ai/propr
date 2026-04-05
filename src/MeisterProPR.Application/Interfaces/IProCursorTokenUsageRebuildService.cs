// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Rebuilds selected ProCursor token usage rollup intervals from retained raw events.
/// </summary>
public interface IProCursorTokenUsageRebuildService
{
    Task<ProCursorTokenUsageRebuildResponse> RebuildAsync(
        Guid clientId,
        ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default);
}
