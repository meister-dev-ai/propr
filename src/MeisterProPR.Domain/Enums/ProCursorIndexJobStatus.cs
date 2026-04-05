// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Durable lifecycle states for a ProCursor indexing job.
/// </summary>
public enum ProCursorIndexJobStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Superseded = 4,
    Cancelled = 5,
}
