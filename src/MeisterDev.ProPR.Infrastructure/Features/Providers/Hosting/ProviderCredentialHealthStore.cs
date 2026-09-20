// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Reads a connection's credential health, applying the precedence between what the host verified and what
///     the provider family reported.
/// </summary>
/// <remarks>
///     A family whose declaration says its connections have no credential health has none: a key an operator
///     typed has nothing to report, while a grant that can be revoked does.
/// </remarks>
/// <param name="contextFactory">Opens a context per read.</param>
/// <param name="drivers">Resolves the family serving a connection, to read its declaration.</param>
/// <param name="secretProtectionCodec">Unwraps the stored credential, to see whether it states an expiry.</param>
public sealed class ProviderCredentialHealthStore(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    IAiProviderDriverRegistry drivers,
    ISecretProtectionCodec secretProtectionCodec)
{
    /// <summary>Reads the resolved health of one connection.</summary>
    /// <param name="connectionProfileId">The connection to read.</param>
    /// <param name="runtimeClassification">
    ///     What the retry stage concluded from a failed call on this connection, when a caller holds one.
    /// </param>
    /// <param name="ct">Cancels the read.</param>
    public async Task<ProviderCredentialHealthState> GetAsync(
        Guid connectionProfileId,
        ProviderCredentialHealthState? runtimeClassification = null,
        CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == connectionProfileId)
            .Select(profile => new
            {
                profile.ProviderKind,
                profile.AuthMode,
                profile.ProtectedSecret,
                profile.CredentialHealth,
                profile.CredentialHealthCause,
                profile.CredentialHealthChangedAt,
                VerificationStatus = profile.VerificationSnapshot!.Status,
                VerificationSummary = profile.VerificationSnapshot!.Summary,
                VerificationCheckedAt = profile.VerificationSnapshot!.CheckedAt,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return ProviderCredentialHealthState.Unknown;
        }

        var declaration = drivers.IsRegistered(row.ProviderKind)
            ? drivers.GetRequired(row.ProviderKind).Declaration
            : null;

        if (declaration?.HasCredentialHealth != true)
        {
            return ProviderCredentialHealthState.Unknown;
        }

        return ProviderCredentialHealthResolver.Resolve(
            ProviderCredentialHealthResolver.FromVerification(
                row.VerificationStatus,
                row.VerificationSummary,
                row.VerificationCheckedAt),
            ProviderCredentialHealthResolver.FromReport(
                row.CredentialHealth,
                row.CredentialHealthCause,
                row.CredentialHealthChangedAt),
            runtimeClassification,
            this.MissingExpiry(row.ProtectedSecret, row.AuthMode));
    }

    private bool MissingExpiry(string? protectedSecret, string authMode)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return false;
        }

        try
        {
            var envelope = ProviderSecretEnvelope.Decode(
                secretProtectionCodec.Unprotect(protectedSecret, ProviderCredentialSessions.SecretPurpose),
                authMode);

            return envelope.Fields.Count > 0 && envelope.ExpiresAt is null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A credential this host cannot unprotect or decode is one a key rotation or a repoint left behind.
            // Reading the health of a connection is a read, and throwing out of it takes the console's view of
            // every connection with it; the stated expiry is one of several things the state is built from, and
            // the connection is described without it rather than not described at all.
            return false;
        }
    }
}
