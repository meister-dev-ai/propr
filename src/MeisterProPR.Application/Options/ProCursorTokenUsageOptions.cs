// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configuration options for ProCursor token usage capture, aggregation, and retention.
///     Bound from <c>PROCURSOR_TOKEN_USAGE_*</c> environment variables and validated on startup.
/// </summary>
public sealed class ProCursorTokenUsageOptions
{
    /// <summary>Polling interval, in seconds, for the ProCursor token rollup worker.</summary>
    [Range(10, 86400, ErrorMessage = "RollupPollSeconds must be between 10 and 86400.")]
    public int RollupPollSeconds { get; set; } = 900;

    /// <summary>Retention period, in days, for raw ProCursor token usage events.</summary>
    [Range(1, 3650, ErrorMessage = "EventRetentionDays must be between 1 and 3650.")]
    public int EventRetentionDays { get; set; } = 365;

    /// <summary>Retention period, in days, for aggregated ProCursor token rollups.</summary>
    [Range(1, 3650, ErrorMessage = "RollupRetentionDays must be between 1 and 3650.")]
    public int RollupRetentionDays { get; set; } = 730;
}
