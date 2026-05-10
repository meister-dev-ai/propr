// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.ProCursor.Infrastructure.Remote;

/// <summary>
///     In-memory runtime configuration cache owned by the extracted ProCursor host.
/// </summary>
public interface IProCursorRuntimeConfigurationCache
{
    /// <summary>
    ///     Hydrates the cache with the current enabled projections from ProPR.
    /// </summary>
    Task WarmAsync(CancellationToken ct = default);

    /// <summary>
    ///     Invalidates one cached source projection.
    /// </summary>
    Task InvalidateAsync(Guid sourceId, CancellationToken ct = default);
}
