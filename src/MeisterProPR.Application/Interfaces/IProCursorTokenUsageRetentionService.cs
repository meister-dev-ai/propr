// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Purges expired ProCursor token reporting data according to the configured retention policy.
/// </summary>
public interface IProCursorTokenUsageRetentionService
{
    Task<ProCursorTokenUsageRetentionResult> PurgeExpiredAsync(CancellationToken ct = default);
}
