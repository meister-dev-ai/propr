// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Service-side broker used to hydrate ProCursor runtime configuration from ProPR.
/// </summary>
public interface IProCursorRuntimeConfigurationBroker
{
    /// <summary>
    ///     Lists the current enabled runtime projections for startup and scheduler warmup.
    /// </summary>
    Task<IReadOnlyList<ProCursorRuntimeConfigurationProjectionDto>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>
    ///     Refreshes runtime configuration for one source.
    /// </summary>
    Task<ProCursorRuntimeConfigurationProjectionDto> RefreshAsync(
        Guid sourceId,
        ProCursorRuntimeConfigurationRefreshRequest request,
        CancellationToken ct = default);
}
