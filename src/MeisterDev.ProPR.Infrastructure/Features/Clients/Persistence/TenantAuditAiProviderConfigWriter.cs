// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Writes provider-configuration changes into the tenant audit trail that already records tenant
///     administration.
/// </summary>
/// <remarks>
///     A second audit store would mean two places to look and two retention policies to keep aligned, so this
///     extends the existing one. A client-scoped profile is attributed to its owning tenant, because the tenant is
///     the boundary an audit trail is read at. An entry that cannot be attributed is dropped rather than written
///     against a guessed tenant: an audit trail that is wrong is worse than one with a gap.
/// </remarks>
public sealed partial class TenantAuditAiProviderConfigWriter(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    IHttpContextAccessor? httpContextAccessor = null,
    ILogger<TenantAuditAiProviderConfigWriter>? logger = null) : IAiProviderConfigAuditWriter
{
    /// <inheritdoc />
    public async Task RecordAsync(AiProviderConfigAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var tenantId = await ResolveTenantIdAsync(db, entry, ct).ConfigureAwait(false);
            if (tenantId is null)
            {
                if (logger is not null)
                {
                    LogUnattributableEntry(logger, entry.Action, entry.ConnectionId);
                }

                return;
            }

            db.TenantAuditEntries.Add(
                new TenantAuditEntryRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId.Value,
                    ActorUserId = ResolveActorUserId(httpContextAccessor),
                    EventType = $"ai.connection.{entry.Action}",
                    Summary = $"AI connection '{entry.DisplayName}' {entry.Action} ({entry.ProviderKind}).",
                    Detail = BuildDetail(entry),
                    OccurredAt = DateTimeOffset.UtcNow,
                });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (logger is not null)
        {
            // Best-effort: the configuration change has already been committed and reported as successful, so
            // failing here would leave the operator's screen and the database disagreeing.
            LogAuditWriteFailed(logger, entry.Action, entry.ConnectionId, exception);
        }
    }

    private static string BuildDetail(AiProviderConfigAuditEntry entry)
    {
        // The credential appears only as whether it was replaced. An audit trail is read by more people than the
        // configuration screen is.
        var scope = entry.TenantId is { } tenantScoped
            ? $"tenantId={tenantScoped}"
            : $"clientId={entry.ClientId}";

        return $"connectionId={entry.ConnectionId}; providerKind={entry.ProviderKind}; baseUrl={entry.BaseUrl}; "
               + $"{scope}; credential={(entry.CredentialChanged ? "replaced" : "unchanged")}";
    }

    private static async Task<Guid?> ResolveTenantIdAsync(
        MeisterProPRDbContext db,
        AiProviderConfigAuditEntry entry,
        CancellationToken ct)
    {
        if (entry.TenantId is { } tenantId && tenantId != Guid.Empty)
        {
            return tenantId;
        }

        if (entry.ClientId is not { } clientId || clientId == Guid.Empty)
        {
            return null;
        }

        var owningTenantId = await db.Clients
            .AsNoTracking()
            .Where(client => client.Id == clientId)
            .Select(client => client.TenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return owningTenantId == Guid.Empty ? null : owningTenantId;
    }

    private static Guid? ResolveActorUserId(IHttpContextAccessor? httpContextAccessor)
    {
        var rawUserId = httpContextAccessor?.HttpContext?.Items["UserId"] as string;
        return Guid.TryParse(rawUserId, out var actorUserId) ? actorUserId : null;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Provider-config audit entry for {Action} of connection {ConnectionId} has no resolvable tenant and was not written.")]
    private static partial void LogUnattributableEntry(ILogger logger, string action, Guid connectionId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Provider-config audit entry for {Action} of connection {ConnectionId} could not be written.")]
    private static partial void LogAuditWriteFailed(ILogger logger, string action, Guid connectionId, Exception exception);
}
