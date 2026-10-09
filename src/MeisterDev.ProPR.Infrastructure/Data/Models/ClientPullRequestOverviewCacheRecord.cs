// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>Shared PostgreSQL source and metadata observations, refresh ownership, and immutable overview generations.</summary>
/// <remarks>
/// Refresh owners, cooldowns and fences coordinate callers across application instances.
/// Denial invalidation persists independently of serving deadlines.
/// Expiration limits serving eligibility; it does not schedule physical row deletion.
/// Write-time cleanup is bounded, so inactive clients can retain expired rows until later writes.
/// </remarks>
public sealed class ClientPullRequestOverviewCacheRecord
{
    public Guid ClientId { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "source";
    public string Fingerprint { get; set; } = "";
    public Guid? ConnectionId { get; set; }
    public string? SourceKey { get; set; }
    public string? Content { get; set; }

    /// <summary>Accounted payload bytes; this value does not measure physical PostgreSQL storage.</summary>
    public int ContentBytes { get; set; }

    public string? Failure { get; set; }
    public Guid? Owner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset NextRefreshAt { get; set; }

    /// <summary>Logical serving deadline, not a physical deletion time.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
