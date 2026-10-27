// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Features.TenantAudit;

/// <summary>One entry of the audit history of a tenant.</summary>
/// <param name="Id">The entry.</param>
/// <param name="TenantId">The tenant the entry belongs to.</param>
/// <param name="EventType">What happened, for example <c>tenant.membership.assigned</c> or <c>elevation.content_read</c>.</param>
/// <param name="Summary">One sentence that describes the event.</param>
/// <param name="Detail">Further detail of the event, when there is any.</param>
/// <param name="ActorUserId">The account that caused the event, or null when ProPR caused it or the account was deleted.</param>
/// <param name="ActorName">The user name of that account, when it still exists.</param>
/// <param name="ElevationId">The elevation of a platform administrator the event belongs to, when it belongs to one.</param>
/// <param name="OccurredAt">When the event happened.</param>
public sealed record TenantAuditEntryDto(
    Guid Id,
    Guid TenantId,
    string EventType,
    string Summary,
    string? Detail,
    Guid? ActorUserId,
    string? ActorName,
    Guid? ElevationId,
    DateTimeOffset OccurredAt);

/// <summary>Reads the audit history of a tenant, newest entry first.</summary>
public interface ITenantAuditHistoryReader
{
    /// <summary>Returns one page of the audit history of the tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="page">The page, starting at 1.</param>
    /// <param name="pageSize">The entries per page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The page, or null when the tenant does not exist.</returns>
    Task<PagedResult<TenantAuditEntryDto>?> GetAsync(Guid tenantId, int page, int pageSize, CancellationToken ct = default);
}
