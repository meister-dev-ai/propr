// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Describes the outcome of a single thread-lifecycle state machine evaluation recorded in
///     <c>memory_activity_log</c>.
/// </summary>
public enum MemoryActivityAction
{
    /// <summary>An embedding was created or updated for a newly-resolved thread.</summary>
    Stored = 0,

    /// <summary>An existing embedding was removed because the thread was reopened or admin-deleted.</summary>
    Removed = 1,

    /// <summary>No memory store action was taken (thread already in target state).</summary>
    NoOp = 2,
}
