// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.TenantAudit;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.PlatformAdminElevation;

/// <summary>Reads the audit history of a tenant from <c>tenant_audit_entries</c>, newest entry first.</summary>
public sealed class TenantAuditHistoryReader(MeisterProPRDbContext database) : ITenantAuditHistoryReader
{
    /// <inheritdoc />
    public async Task<PagedResult<TenantAuditEntryDto>?> GetAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        if (!await database.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId, ct))
        {
            return null;
        }

        var query = database.TenantAuditEntries.AsNoTracking().Where(entry => entry.TenantId == tenantId);
        var total = await query.CountAsync(ct);
        if (PageOffset.Of(page, pageSize, total) is not { } offset)
        {
            return new PagedResult<TenantAuditEntryDto>([], total, page, pageSize);
        }

        var items = await query
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Skip(offset)
            .Take(pageSize)
            .Select(entry => new TenantAuditEntryDto(
                entry.Id,
                entry.TenantId,
                entry.EventType,
                entry.Summary,
                entry.Detail,
                entry.ActorUserId,
                entry.ActorUser != null ? entry.ActorUser.Username : null,
                entry.ElevationId,
                entry.OccurredAt))
            .ToListAsync(ct);
        return new PagedResult<TenantAuditEntryDto>(items, total, page, pageSize);
    }
}
