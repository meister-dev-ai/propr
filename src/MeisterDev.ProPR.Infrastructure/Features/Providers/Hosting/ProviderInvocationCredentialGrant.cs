// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Infrastructure.Data;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     What the host checks, and what it records as the owner, when a credential an action produced is stored.
/// </summary>
/// <remarks>
///     <para>
///         The checks made when the action started are not checks at the moment the credential is written. An
///         invocation outlives the call that opened it — an operator signing in at a vendor takes minutes — and a
///         role can be withdrawn and an entitlement can lapse in between. Both are read again here, and the write
///         is refused when either has gone.
///     </para>
///     <para>
///         The owner recorded is the administrator who <em>initiated</em> the invocation, never whoever completed
///         it. A grant is personal: it is issued against one administrator's account at the provider and revoking
///         it means revoking that account's grant. On the paste path any administrator holding the connection's
///         owner role can submit another administrator's callback value, so recording the submitter would point a
///         later revocation at the wrong person.
///     </para>
///     <para>
///         Held by the host and passed to the credential session, so it applies wherever a credential is stored
///         during an invocation and a family has no way to skip it: a family reaches a stored credential through
///         the session and through nothing else.
///     </para>
/// </remarks>
/// <param name="Connection">The connection the credential is stored against, which carries its owner.</param>
/// <param name="Binding">The family serving the connection and the capability it declared.</param>
/// <param name="OwnerAdminId">The administrator who initiated the invocation, recorded as the grant's owner.</param>
/// <param name="OwnerDisplayName">What that administrator is called, recorded beside the identifier.</param>
/// <param name="Capabilities">The licence check, read again at the moment of the write.</param>
/// <param name="OwnerRoles">The owner-role check, read again at the moment of the write.</param>
public sealed record ProviderInvocationCredentialGrant(
    AiConnectionDto Connection,
    ProviderAddInBinding Binding,
    Guid? OwnerAdminId,
    string? OwnerDisplayName,
    IProviderAddInCapabilityGate Capabilities,
    IProviderConnectionOwnerRoles OwnerRoles)
{
    /// <summary>
    ///     Refuses the store when the declared capability or the owner role has gone since the action started.
    /// </summary>
    /// <remarks>
    ///     Both are read through <paramref name="db" />, which is the context the credential is written on, so the
    ///     answers and the write are one transaction. The entitlement is resolved rather than taken from the held
    ///     answer the review path uses: that answer can be a minute old, and a minute is long enough for the
    ///     entitlement this write depends on to have lapsed before the action even started.
    /// </remarks>
    /// <param name="db">The context the credential is written on.</param>
    /// <param name="ct">Cancels the checks.</param>
    /// <exception cref="ProviderCapabilityUnavailableException">The installation is no longer entitled.</exception>
    /// <exception cref="ProviderAuthorizationWithdrawnException">The initiating administrator no longer holds the role.</exception>
    public async Task RefuseWhenWithdrawnAsync(MeisterProPRDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!await this.Capabilities.IsAvailableNowAsync(this.Binding.RequiredCapabilityKey, ct).ConfigureAwait(false))
        {
            throw new ProviderCapabilityUnavailableException(
                $"The provider family '{this.Binding.AddInKey}' requires the "
                + $"'{this.Binding.RequiredCapabilityKey}' capability, which this installation's licence no "
                + "longer makes available, so the credential this action produced was not stored.");
        }

        if (!await this.OwnerRoles.HoldsOwnerRoleAsync(db, this.Connection, this.OwnerAdminId, ct)
                .ConfigureAwait(false))
        {
            throw new ProviderAuthorizationWithdrawnException(
                "The administrator who started this action is no longer "
                + $"{this.OwnerRoles.DescribeRequirement(this.Connection)}, so the credential it produced was "
                + "not stored.");
        }
    }
}
