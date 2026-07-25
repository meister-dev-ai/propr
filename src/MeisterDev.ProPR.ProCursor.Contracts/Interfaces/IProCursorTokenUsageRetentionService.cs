// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Purges expired ProCursor token reporting data according to the configured retention policy.
/// </summary>
public interface IProCursorTokenUsageRetentionService
{
    /// <summary>
    ///     Purges expired ProCursor token usage records asynchronously.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result of the purge operation.</returns>
    Task<ProCursorTokenUsageRetentionResult> PurgeExpiredAsync(CancellationToken ct = default);
}
