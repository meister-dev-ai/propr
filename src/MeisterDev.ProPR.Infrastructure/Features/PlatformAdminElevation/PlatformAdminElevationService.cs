// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Application.Features.TenantAudit;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.PlatformAdminElevation;

/// <summary>
///     Stores the elevations of platform administrators, decides which content a platform administrator may read, and
///     records starts, ends, expiries and content reads in the audit history of the tenant.
/// </summary>
/// <remarks>
///     A host without a database registers no <see cref="MeisterProPRDbContext" />. Such a host stores no tenants, clients
///     or reviews, so every client is unknown here and the service answers that no client exists.
/// </remarks>
public sealed class PlatformAdminElevationService(
    IOptions<PlatformAdminElevationOptions> options,
    MeisterProPRDbContext? database = null,
    TimeProvider? clock = null) : IPlatformAdminElevationService
{
    private const int SummaryLength = 256;
    private const int DetailLength = 2048;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public int MaxDurationMinutes => options.Value.MaxDurationMinutes;

    /// <inheritdoc />
    public async Task<StartElevationResult> StartAsync(
        Guid administratorUserId,
        Guid tenantId,
        Guid? clientId,
        string reason,
        int durationMinutes,
        CancellationToken ct = default)
    {
        var db = this.RequireDatabase();
        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length is 0 or > PlatformAdminElevationLimits.MaxReasonLength)
        {
            return new StartElevationResult(StartElevationOutcome.InvalidReason, null);
        }

        if (durationMinutes < 1 || durationMinutes > this.MaxDurationMinutes)
        {
            return new StartElevationResult(StartElevationOutcome.InvalidDuration, null);
        }

        if (TenantCatalog.IsSystemTenant(tenantId))
        {
            return new StartElevationResult(StartElevationOutcome.SystemTenant, null);
        }

        var administratorName = await db.AppUsers.AsNoTracking()
            .Where(user => user.Id == administratorUserId && user.IsActive && user.GlobalRole == AppUserRole.Admin)
            .Select(user => user.Username)
            .FirstOrDefaultAsync(ct);
        if (administratorName is null)
        {
            return new StartElevationResult(StartElevationOutcome.NotPlatformAdministrator, null);
        }

        var tenant = await db.Tenants.AsNoTracking()
            .Where(row => row.Id == tenantId)
            .Select(row => new { row.Id, row.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (tenant is null)
        {
            return new StartElevationResult(StartElevationOutcome.TenantNotFound, null);
        }

        string? clientName = null;
        if (clientId is { } scopedClientId)
        {
            clientName = await db.Clients.AsNoTracking()
                .Where(client => client.Id == scopedClientId && client.TenantId == tenantId)
                .Select(client => client.DisplayName)
                .FirstOrDefaultAsync(ct);
            if (clientName is null)
            {
                return new StartElevationResult(StartElevationOutcome.ClientNotFound, null);
            }
        }

        var now = this._clock.GetUtcNow();
        var record = new PlatformAdminElevationRecord
        {
            Id = Guid.NewGuid(),
            AdministratorUserId = administratorUserId,
            AdministratorName = administratorName,
            TenantId = tenantId,
            ClientId = clientId,
            Reason = trimmedReason,
            StartedAt = now,
            ExpiresAt = now.AddMinutes(durationMinutes),
            ConcurrencyStamp = Guid.NewGuid(),
        };

        db.PlatformAdminElevations.Add(record);
        db.TenantAuditEntries.Add(
            AuditEntry(
                record,
                administratorUserId,
                PlatformAdminElevationAuditEvents.Started,
                $"Platform administrator {administratorName} started an elevation for {ScopeLabel(clientName)} until "
                + $"{FormatTime(record.ExpiresAt)}.",
                $"Reason: {trimmedReason}",
                now));
        await db.SaveChangesAsync(ct);

        return new StartElevationResult(
            StartElevationOutcome.Started,
            ToDto(record, tenant.DisplayName, clientName, 0, now));
    }

    /// <inheritdoc />
    public async Task<EndElevationResult> EndAsync(Guid elevationId, Guid actorUserId, CancellationToken ct = default)
    {
        var db = this.RequireDatabase();
        var now = this._clock.GetUtcNow();
        var record = await db.PlatformAdminElevations.FirstOrDefaultAsync(elevation => elevation.Id == elevationId, ct);
        if (record is null)
        {
            return new EndElevationResult(EndElevationOutcome.NotFound, null);
        }

        if (record.EndedAt is not null || record.ExpiresAt <= now)
        {
            return new EndElevationResult(EndElevationOutcome.AlreadyOver, await this.GetAsync(elevationId, ct));
        }

        var actorName = await db.AppUsers.AsNoTracking()
                            .Where(user => user.Id == actorUserId)
                            .Select(user => user.Username)
                            .FirstOrDefaultAsync(ct)
                        ?? actorUserId.ToString();
        var clientName = await ClientNameAsync(db, record.ClientId, ct);

        record.EndedAt = now;
        record.EndedByUserId = actorUserId;
        record.EndedByName = actorName;
        record.ConcurrencyStamp = Guid.NewGuid();
        db.TenantAuditEntries.Add(
            AuditEntry(
                record,
                actorUserId,
                PlatformAdminElevationAuditEvents.Ended,
                actorName == record.AdministratorName
                    ? $"Platform administrator {actorName} ended their elevation for {ScopeLabel(clientName)}."
                    : $"Platform administrator {actorName} ended the elevation of {record.AdministratorName} for "
                      + $"{ScopeLabel(clientName)}.",
                $"Reason of the elevation: {record.Reason}",
                now));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request ended the elevation, or recorded its expiry, between the read and this write.
            db.ChangeTracker.Clear();
            return new EndElevationResult(EndElevationOutcome.AlreadyOver, await this.GetAsync(elevationId, ct));
        }

        return new EndElevationResult(EndElevationOutcome.Ended, await this.GetAsync(elevationId, ct));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlatformAdminElevationDto>> GetActiveAsync(Guid administratorUserId, CancellationToken ct = default)
    {
        if (database is null)
        {
            return [];
        }

        var now = this._clock.GetUtcNow();
        return await this.ProjectAsync(
            database.PlatformAdminElevations.AsNoTracking()
                .Where(elevation => elevation.AdministratorUserId == administratorUserId
                                    && elevation.EndedAt == null
                                    && elevation.ExpiresAt > now)
                .OrderBy(elevation => elevation.ExpiresAt),
            now,
            ct);
    }

    /// <inheritdoc />
    public async Task<PagedResult<PlatformAdminElevationDto>> ListAsync(
        Guid? tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        if (database is null)
        {
            return new PagedResult<PlatformAdminElevationDto>([], 0, page, pageSize);
        }

        var query = database.PlatformAdminElevations.AsNoTracking();
        if (tenantId is { } tenant)
        {
            query = query.Where(elevation => elevation.TenantId == tenant);
        }

        var total = await query.CountAsync(ct);
        if (PageOffset.Of(page, pageSize, total) is not { } offset)
        {
            return new PagedResult<PlatformAdminElevationDto>([], total, page, pageSize);
        }

        var items = await this.ProjectAsync(
            query.OrderByDescending(elevation => elevation.StartedAt)
                .ThenBy(elevation => elevation.Id)
                .Skip(offset)
                .Take(pageSize),
            this._clock.GetUtcNow(),
            ct);
        return new PagedResult<PlatformAdminElevationDto>(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<PlatformAdminElevationDto?> GetAsync(Guid elevationId, CancellationToken ct = default)
    {
        if (database is null)
        {
            return null;
        }

        var items = await this.ProjectAsync(
            database.PlatformAdminElevations.AsNoTracking().Where(elevation => elevation.Id == elevationId),
            this._clock.GetUtcNow(),
            ct);
        return items.Count == 0 ? null : items[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantAuditEntryDto>> GetAuditEntriesAsync(Guid elevationId, CancellationToken ct = default)
    {
        if (database is null)
        {
            return [];
        }

        return await database.TenantAuditEntries.AsNoTracking()
            .Where(entry => entry.ElevationId == elevationId)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
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
    }

    /// <inheritdoc />
    public async Task<Guid?> GetReviewClientIdAsync(Guid reviewJobOrThreadPassId, CancellationToken ct = default)
    {
        if (database is null)
        {
            return null;
        }

        var jobClient = await database.ReviewJobs.AsNoTracking()
            .Where(job => job.Id == reviewJobOrThreadPassId)
            .Select(job => (Guid?)job.ClientId)
            .FirstOrDefaultAsync(ct);
        if (jobClient is not null)
        {
            return jobClient;
        }

        return await database.ThreadPassJobs.AsNoTracking()
            .Where(pass => pass.Id == reviewJobOrThreadPassId)
            .Select(pass => (Guid?)pass.ClientId)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentAccessGrant>> ResolveAsync(
        Guid administratorUserId,
        IReadOnlyCollection<Guid> clientIds,
        CancellationToken ct = default)
    {
        if (database is null || clientIds.Count == 0)
        {
            return [];
        }

        var ids = clientIds.Distinct().ToList();
        var clients = await database.Clients.AsNoTracking()
            .Where(client => ids.Contains(client.Id))
            .Select(client => new ClientRow(client.Id, client.DisplayName, client.TenantId))
            .ToListAsync(ct);
        return await this.GrantAsync(administratorUserId, clients, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentAccessGrant>> ResolveReadableAsync(Guid administratorUserId, CancellationToken ct = default)
    {
        if (database is null)
        {
            return [];
        }

        var active = await this.LoadActiveAsync(administratorUserId, ct);
        var elevatedTenants = active.Where(elevation => elevation.ClientId is null).Select(elevation => elevation.TenantId).ToList();
        var elevatedClients = active.Where(elevation => elevation.ClientId is not null).Select(elevation => elevation.ClientId!.Value).ToList();
        var systemTenantId = TenantCatalog.SystemTenantId;
        var clients = await database.Clients.AsNoTracking()
            .Where(client => client.TenantId == Guid.Empty
                             || client.TenantId == systemTenantId
                             || elevatedTenants.Contains(client.TenantId)
                             || elevatedClients.Contains(client.Id))
            .Select(client => new ClientRow(client.Id, client.DisplayName, client.TenantId))
            .ToListAsync(ct);
        return Grant(clients, active);
    }

    /// <inheritdoc />
    public async Task RecordReadsAsync(Guid administratorUserId, IReadOnlyList<ContentRead> reads, CancellationToken ct = default)
    {
        if (reads.Count == 0)
        {
            return;
        }

        var db = this.RequireDatabase();
        var now = this._clock.GetUtcNow();
        foreach (var read in reads)
        {
            var administratorName = read.Elevation.AdministratorName;
            var clients = read.Clients.Count == 1
                ? $"client {read.Clients[0].ClientDisplayName}"
                : $"{read.Clients.Count} clients";
            var detail = new List<string> { $"Request: {read.Request}." };
            if (read.ReviewJobId is { } reviewJobId)
            {
                detail.Add($"Review job: {reviewJobId}.");
            }

            detail.Add($"Clients: {string.Join(", ", read.Clients.Select(client => $"{client.ClientDisplayName} ({client.ClientId})"))}.");
            detail.Add($"Elevation for {ScopeLabel(read.Elevation.ClientDisplayName)} until {FormatTime(read.Elevation.ExpiresAt)}.");
            detail.Add($"Reason: {read.Elevation.Reason}");

            db.TenantAuditEntries.Add(
                new TenantAuditEntryRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = read.Elevation.TenantId,
                    ActorUserId = administratorUserId,
                    EventType = PlatformAdminElevationAuditEvents.ContentRead,
                    Summary = Limit($"Platform administrator {administratorName} read {read.Resource} of {clients}.", SummaryLength),
                    Detail = Limit(string.Join(" ", detail), DetailLength),
                    ElevationId = read.Elevation.Id,
                    OccurredAt = now,
                });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> RecordExpiriesAsync(CancellationToken ct = default)
    {
        var db = this.RequireDatabase();
        var now = this._clock.GetUtcNow();
        var expired = await db.PlatformAdminElevations.AsNoTracking()
            .Where(elevation => elevation.EndedAt == null && elevation.ExpiresAt <= now)
            .OrderBy(elevation => elevation.ExpiresAt)
            .Select(elevation => elevation.Id)
            .Take(200)
            .ToListAsync(ct);

        var recorded = 0;
        foreach (var id in expired)
        {
            var record = await db.PlatformAdminElevations
                .FirstOrDefaultAsync(elevation => elevation.Id == id && elevation.EndedAt == null, ct);
            if (record is null)
            {
                continue;
            }

            var clientName = await ClientNameAsync(db, record.ClientId, ct);
            record.EndedAt = record.ExpiresAt;
            record.ConcurrencyStamp = Guid.NewGuid();
            db.TenantAuditEntries.Add(
                AuditEntry(
                    record,
                    null,
                    PlatformAdminElevationAuditEvents.Expired,
                    $"The elevation of platform administrator {record.AdministratorName} for {ScopeLabel(clientName)} expired.",
                    $"Reason of the elevation: {record.Reason}",
                    record.ExpiresAt));
            try
            {
                await db.SaveChangesAsync(ct);
                recorded++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another instance recorded this expiry, or an administrator ended the elevation, in the meantime.
                db.ChangeTracker.Clear();
            }
        }

        return recorded;
    }

    private static string ScopeLabel(string? clientName) =>
        clientName is null ? "every client of the tenant" : $"client {clientName}";

    private static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Limit(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";

    private static TenantAuditEntryRecord AuditEntry(
        PlatformAdminElevationRecord elevation,
        Guid? actorUserId,
        string eventType,
        string summary,
        string detail,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = elevation.TenantId,
            ActorUserId = actorUserId,
            EventType = eventType,
            Summary = Limit(summary, SummaryLength),
            Detail = Limit(detail, DetailLength),
            ElevationId = elevation.Id,
            OccurredAt = occurredAt,
        };

    private static async Task<string?> ClientNameAsync(MeisterProPRDbContext db, Guid? clientId, CancellationToken ct) =>
        clientId is { } id
            ? await db.Clients.AsNoTracking().Where(client => client.Id == id).Select(client => client.DisplayName).FirstOrDefaultAsync(ct)
              ?? id.ToString()
            : null;

    private static PlatformAdminElevationState StateAt(DateTimeOffset expiresAt, DateTimeOffset? endedAt, DateTimeOffset now)
    {
        if (endedAt is { } ended && ended < expiresAt)
        {
            return PlatformAdminElevationState.Ended;
        }

        return endedAt is null && expiresAt > now ? PlatformAdminElevationState.Active : PlatformAdminElevationState.Expired;
    }

    private static PlatformAdminElevationDto ToDto(
        PlatformAdminElevationRecord record,
        string tenantName,
        string? clientName,
        int contentReads,
        DateTimeOffset now) =>
        new(
            record.Id,
            record.AdministratorUserId,
            record.AdministratorName,
            record.TenantId,
            tenantName,
            record.ClientId,
            clientName,
            record.Reason,
            record.StartedAt,
            record.ExpiresAt,
            record.EndedAt,
            record.EndedByName,
            StateAt(record.ExpiresAt, record.EndedAt, now),
            contentReads);

    /// <summary>
    ///     Decides the content of each client for a platform administrator. A role the administrator holds on a client
    ///     through a membership or an assignment grants no content, because a platform administrator can give themselves
    ///     a membership in any tenant. Outside the System tenant only an elevation grants content.
    /// </summary>
    private static IReadOnlyList<ContentAccessGrant> Grant(
        IEnumerable<ClientRow> clients,
        IReadOnlyList<PlatformAdminElevationDto> active)
    {
        var grants = new List<ContentAccessGrant>();
        foreach (var client in clients)
        {
            if (IsInstallationClient(client))
            {
                grants.Add(new ContentAccessGrant(client.Id, client.DisplayName, client.TenantId, ContentAccessKind.Installation));
                continue;
            }

            // An elevation scoped to the client is preferred over one for the whole tenant, so a read is recorded under
            // the narrowest grant the administrator holds.
            var elevation = active.FirstOrDefault(candidate => candidate.TenantId == client.TenantId && candidate.ClientId == client.Id)
                            ?? active.FirstOrDefault(candidate => candidate.TenantId == client.TenantId && candidate.ClientId is null);
            grants.Add(
                elevation is null
                    ? new ContentAccessGrant(client.Id, client.DisplayName, client.TenantId, ContentAccessKind.Refused)
                    : new ContentAccessGrant(client.Id, client.DisplayName, client.TenantId, ContentAccessKind.Elevation, elevation));
        }

        return grants;
    }

    // A client without a tenant counts as a client of the System tenant, as it does everywhere else in ProPR. A migrated
    // database holds no such client, because the tenant of a client is required and refers to an existing tenant.
    private static bool IsInstallationClient(ClientRow client) =>
        client.TenantId == Guid.Empty || TenantCatalog.IsSystemTenant(client.TenantId);

    private async Task<IReadOnlyList<ContentAccessGrant>> GrantAsync(
        Guid administratorUserId,
        IReadOnlyList<ClientRow> clients,
        CancellationToken ct)
    {
        var active = clients.Any(client => !IsInstallationClient(client))
            ? await this.LoadActiveAsync(administratorUserId, ct)
            : [];
        return Grant(clients, active);
    }

    private Task<IReadOnlyList<PlatformAdminElevationDto>> LoadActiveAsync(Guid administratorUserId, CancellationToken ct) =>
        this.GetActiveAsync(administratorUserId, ct);

    private async Task<IReadOnlyList<PlatformAdminElevationDto>> ProjectAsync(
        IQueryable<PlatformAdminElevationRecord> query,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var db = this.RequireDatabase();
        var readEvent = PlatformAdminElevationAuditEvents.ContentRead;
        var rows = await query
            .Select(elevation => new
            {
                Record = elevation,
                TenantName = db.Tenants.Where(tenant => tenant.Id == elevation.TenantId).Select(tenant => tenant.DisplayName).FirstOrDefault(),
                ClientName = elevation.ClientId == null
                    ? null
                    : db.Clients.Where(client => client.Id == elevation.ClientId).Select(client => client.DisplayName).FirstOrDefault(),
                Reads = db.TenantAuditEntries.Count(entry => entry.ElevationId == elevation.Id && entry.EventType == readEvent),
            })
            .ToListAsync(ct);
        return rows
            .Select(row => ToDto(row.Record, row.TenantName ?? row.Record.TenantId.ToString(), row.ClientName, row.Reads, now))
            .ToList();
    }

    private MeisterProPRDbContext RequireDatabase() =>
        database ?? throw new InvalidOperationException("Elevations need the ProPR database, and this host has none.");

    private sealed record ClientRow(Guid Id, string DisplayName, Guid TenantId);
}
