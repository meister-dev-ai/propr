// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Records safe, idempotent ProCursor token usage events without disrupting the primary workflow.
/// </summary>
public interface IProCursorTokenUsageRecorder
{
    /// <summary>
    ///     Attempts to persist one ProCursor token usage event. Duplicate request identities are ignored.
    /// </summary>
    Task RecordAsync(ProCursorTokenUsageCaptureRequest request, CancellationToken ct = default);
}
