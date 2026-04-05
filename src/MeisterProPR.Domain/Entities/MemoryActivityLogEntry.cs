// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Append-only crawl-side audit record.  One row per state machine evaluation per thread per crawl cycle.
///     Persisted in the <c>memory_activity_log</c> table — never updated or deleted by the system.
/// </summary>
public sealed class MemoryActivityLogEntry
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning client identifier.</summary>
    public Guid ClientId { get; set; }

    /// <summary>ADO thread identifier.</summary>
    public int ThreadId { get; set; }

    /// <summary>ADO repository identifier (≤ 256 chars).</summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>ADO pull request number.</summary>
    public int PullRequestId { get; set; }

    /// <summary>State machine decision for this evaluation.</summary>
    public MemoryActivityAction Action { get; set; }

    /// <summary>The <c>last_seen_status</c> value before this crawl cycle. Null for brand-new threads.</summary>
    public string? PreviousStatus { get; set; }

    /// <summary>The ADO thread status observed this crawl cycle.</summary>
    public string CurrentStatus { get; set; } = string.Empty;

    /// <summary>
    ///     For no-ops: <c>already_resolved</c> or <c>still_active</c>.
    ///     For admin deletes: <c>admin_deleted</c>.
    ///     For failures: brief error summary.
    ///     Null for successful store/remove actions.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>UTC timestamp when the state machine evaluated this thread.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
