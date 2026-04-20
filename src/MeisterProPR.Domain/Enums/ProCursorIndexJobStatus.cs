// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Durable lifecycle states for a ProCursor indexing job.
/// </summary>
public enum ProCursorIndexJobStatus
{
    /// <summary>Job is awaiting processing.</summary>
    Pending = 0,
    /// <summary>Job is currently being processed.</summary>
    Processing = 1,
    /// <summary>Job has completed successfully.</summary>
    Completed = 2,
    /// <summary>Job has failed.</summary>
    Failed = 3,
    /// <summary>Job has been superseded by another job.</summary>
    Superseded = 4,
    /// <summary>Job has been cancelled.</summary>
    Cancelled = 5,
}
