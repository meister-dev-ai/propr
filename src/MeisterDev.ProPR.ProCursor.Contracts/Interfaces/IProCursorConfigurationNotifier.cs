// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Tells the ProCursor service that the configuration of a knowledge source changed. The ProCursor service caches
///     the configuration it reads from ProPR, and reads a source again before its next use only when it is told.
/// </summary>
public interface IProCursorConfigurationNotifier
{
    /// <summary>Makes the ProCursor service read the configuration of the knowledge source again.</summary>
    /// <param name="sourceId">The knowledge source whose configuration changed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SourceChangedAsync(Guid sourceId, CancellationToken ct = default);
}
