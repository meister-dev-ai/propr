// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Reads the reasoning-capture policy off the tenant row that owns a client, in one query and without a cache.
/// </summary>
/// <remarks>
///     The client and its tenant are read together, so the answer describes one state of the store: two reads
///     could straddle a reassignment and report the policy of a tenant the client no longer belongs to. A
///     lookup that comes up empty answers with the installation default, and so does the System tenant, which
///     has no editable policy surface: both are outcomes of a healthy store, and the installation switch is the
///     behaviour it already has.
///     <para>
///         A lookup that throws answers <see cref="ReasoningCapturePolicy.Disabled" />. The installation switch
///         defaults to capturing, so answering with it would record the reasoning of a tenant that turned
///         capture off, in the one case the policy exists for. The review still runs; it runs without the
///         model's reasoning in its protocol.
///     </para>
/// </remarks>
public sealed partial class TenantReasoningCapturePolicyProvider(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ILogger<TenantReasoningCapturePolicyProvider> logger)
    : ITenantReasoningCapturePolicyProvider
{
    /// <inheritdoc />
    public async Task<ReasoningCapturePolicy> GetForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        if (clientId == Guid.Empty)
        {
            return ReasoningCapturePolicy.InstallationDefault;
        }

        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var owner = await (
                    from client in db.Clients.AsNoTracking()
                    join tenant in db.Tenants.AsNoTracking() on client.TenantId equals tenant.Id
                    where client.Id == clientId
                    select new OwningTenant(tenant.Id, tenant.ReasoningCapturePolicy))
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (owner is null || TenantCatalog.IsSystemTenant(owner.TenantId))
            {
                return ReasoningCapturePolicy.InstallationDefault;
            }

            return owner.Policy;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPolicyLookupFailed(logger, clientId, ex);
            return ReasoningCapturePolicy.Disabled;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The reasoning-capture policy of the tenant owning client {ClientId} could not be read. This job captures no model reasoning, because no tenant has permitted it.")]
    private static partial void LogPolicyLookupFailed(ILogger logger, Guid clientId, Exception exception);

    private sealed record OwningTenant(Guid TenantId, ReasoningCapturePolicy Policy);
}
