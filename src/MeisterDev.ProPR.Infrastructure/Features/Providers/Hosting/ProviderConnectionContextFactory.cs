// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Builds the host primitives one provider family may use against one stored connection.
/// </summary>
/// <remarks>
///     Nothing it builds holds a database context, a request or a dependency-injection scope: every primitive
///     opens a context of its own from the factory when it is called. That is what lets a family keep the set for
///     the life of a client the host built, and what keeps a review's parallel passes from driving one context at
///     once.
/// </remarks>
/// <param name="httpClients">The named pipelines provider traffic leaves through.</param>
/// <param name="contextFactory">Opens a database context per call.</param>
/// <param name="secretProtectionCodec">Wraps and unwraps stored values. A family never sees either form.</param>
/// <param name="capabilities">The licence check made before a credential is handed over.</param>
/// <param name="ownerRoles">The owner-role check made again before a credential an action produced is stored.</param>
/// <param name="drivers">Resolves the family serving a connection, to read its declaration.</param>
/// <param name="invocations">Where an action's terminal state is written.</param>
/// <param name="cancellations">The signals in-flight invocations watch.</param>
/// <param name="timeProvider">Supplies the instant expiries and windows are judged against.</param>
public sealed class ProviderConnectionContextFactory(
    IHttpMessageHandlerFactory httpClients,
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ISecretProtectionCodec secretProtectionCodec,
    IProviderAddInCapabilityGate capabilities,
    IProviderConnectionOwnerRoles ownerRoles,
    IAiProviderDriverRegistry drivers,
    ProviderActionInvocations invocations,
    ProviderInvocationCancellation cancellations,
    TimeProvider timeProvider) : IProviderConnectionContextFactory
{
    /// <inheritdoc />
    public IProviderConnectionContext? ForConnection(AiConnectionDto connection)
    {
        return this.Bind(connection) is { } binding
            ? this.Build(binding, actingPrincipalId: null, grant: null)
            : null;
    }

    /// <summary>
    ///     Builds the set a family may use while one of its action invocations is in flight, with the signal it
    ///     watches and the record it reports against.
    /// </summary>
    /// <param name="connection">The stored connection the action acts on.</param>
    /// <param name="invocation">The invocation the host opened.</param>
    /// <param name="ownerDisplayName">
    ///     What the initiating administrator is called, resolved once when the invocation was opened and recorded
    ///     as the owner of whatever credential the action stores.
    /// </param>
    /// <param name="submittedSecrets">
    ///     The secret-marked values the operator submitted for this invocation. They are credential material the
    ///     family is handed and the host never persists, so they are scrubbed out of whatever the family reports
    ///     alongside the stored credential.
    /// </param>
    /// <returns>The set, or null when the connection's family cannot be resolved.</returns>
    public IProviderActionContext? ForInvocation(
        AiConnectionDto connection,
        ProviderActionInvocation invocation,
        string? ownerDisplayName = null,
        IReadOnlyCollection<string>? submittedSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (this.Bind(connection) is not { } binding)
        {
            return null;
        }

        var grant = new ProviderInvocationCredentialGrant(
            connection,
            binding,
            invocation.InitiatingAdminId,
            ownerDisplayName,
            capabilities,
            ownerRoles);

        // The acting principal rides on the stored entries the action writes, so a completion that carries no
        // session cannot be claimed by an administrator other than the one who started the action. A family
        // never holds it and never supplies it.
        return new ProviderActionContext(
            this.Build(binding, invocation.InitiatingAdminId, grant),
            cancellations.Open(invocation.Id, invocation.ExpiresAt - timeProvider.GetUtcNow()),
            new ProviderInvocationReporter(
                invocation.Id,
                invocations,
                Combined(this.SecretsOf(binding), submittedSecrets)));
    }

    /// <summary>
    ///     What the host knows about the family serving one stored connection, or null when this build cannot
    ///     resolve it.
    /// </summary>
    /// <remarks>
    ///     The same value every primitive in a set is built over. Exposed so the dispatch path can open an
    ///     invocation against it without deriving the family a second time and risking a different answer.
    /// </remarks>
    /// <param name="connection">The stored connection.</param>
    public ProviderAddInBinding? BindingFor(AiConnectionDto connection)
    {
        return this.Bind(connection);
    }

    /// <summary>
    ///     Reads the credential values held for one connection, so a string a family produced can be scrubbed of
    ///     them before it is stored or shown.
    /// </summary>
    /// <remarks>
    ///     Exposed so the dispatch path scrubs against the same values the primitives do. A family's strings
    ///     reach a surface from two directions — through the invocation report it writes and through the result
    ///     it answers a dispatch with — and both have to be scrubbed against one source.
    /// </remarks>
    /// <param name="binding">The connection whose credential is read.</param>
    public Func<CancellationToken, Task<IReadOnlyCollection<string>>> SecretsFor(ProviderAddInBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return this.SecretsOf(binding);
    }

    private ProviderAddInBinding? Bind(AiConnectionDto connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Id == Guid.Empty || !drivers.IsRegistered(connection.ProviderKind))
        {
            return null;
        }

        var declaration = drivers.GetRequired(connection.ProviderKind).Declaration;

        return new ProviderAddInBinding(
            connection.Id,
            connection.DisplayName,
            declaration.Key,
            connection.ProviderKind,
            connection.AuthMode,
            declaration.RequiredCapabilityKey,
            declaration.LegacyNames.Keys);
    }

    /// <summary>The stored credential and the values submitted for one invocation, as one scrub source.</summary>
    /// <param name="stored">The connection's credential, read each time a message needs scrubbing.</param>
    /// <param name="submitted">The submitted secret values, held because nothing stores them.</param>
    private static Func<CancellationToken, Task<IReadOnlyCollection<string>>> Combined(
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> stored,
        IReadOnlyCollection<string>? submitted)
    {
        if (submitted is not { Count: > 0 })
        {
            return stored;
        }

        // Copied before the closure captures it. The caller owns the collection and the closure is called for
        // every message this invocation scrubs, so a caller that cleared or reused it left the scrubber with
        // fewer secrets than the invocation actually holds.
        string[] held = [.. submitted];

        return async ct => [.. await stored(ct).ConfigureAwait(false), .. held];
    }

    private ProviderConnectionContext Build(
        ProviderAddInBinding binding,
        Guid? actingPrincipalId,
        ProviderInvocationCredentialGrant? grant)
    {
        var credentials = new ProviderCredentialSessions(
            binding,
            contextFactory,
            secretProtectionCodec,
            capabilities,
            timeProvider,
            lockWait: null,
            grant);

        return new ProviderConnectionContext(
            new ProviderHttpClientFactory(httpClients),
            credentials,
            new ProviderResourceLeases(binding, contextFactory, timeProvider),
            new ProviderKeyedStore(binding, actingPrincipalId, contextFactory, secretProtectionCodec, timeProvider),
            new ProviderHealthSignal(binding, contextFactory, this.SecretsOf(binding), timeProvider));
    }

    /// <summary>
    ///     Reads the credential values held for one connection, so a message a family produced can be scrubbed
    ///     against them before it is stored or shown.
    /// </summary>
    /// <remarks>
    ///     Read when a message needs scrubbing rather than held, because the set here has to be what is stored
    ///     now: a family that reports after renewing a credential would otherwise be scrubbed against the value
    ///     it replaced. The licence check the credential handle makes is not on this path: a message is
    ///     scrubbed whether or not the installation is still entitled to use the credential in it.
    /// </remarks>
    /// <param name="binding">The connection whose credential is read.</param>
    private Func<CancellationToken, Task<IReadOnlyCollection<string>>> SecretsOf(ProviderAddInBinding binding)
    {
        return async ct =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.AiConnectionProfiles
                .AsNoTracking()
                .Where(profile => profile.Id == binding.ConnectionProfileId)
                .Select(profile => new { profile.ProtectedSecret })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            // A connection deleted while an invocation is open has no credential to scrub against. Decoding the
            // absence reported the same empty set, but only because FirstOrDefaultAsync and a stored null are
            // indistinguishable here; saying so is what keeps that true if decoding stops tolerating null.
            if (row is null)
            {
                return [];
            }

            var stored = row.ProtectedSecret;

            var credential = ProviderCredentialSessions.Decode(
                stored,
                binding.AuthMode,
                secretProtectionCodec,
                binding.AddInKey);

            return [.. credential.Fields.Values];
        };
    }
}
