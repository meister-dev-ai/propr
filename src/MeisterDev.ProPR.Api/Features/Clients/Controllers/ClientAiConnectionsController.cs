// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Manages provider-neutral AI connection profiles for a client.</summary>
[ApiController]
[Route("clients/{clientId:guid}/ai-connections")]
public sealed partial class ClientAiConnectionsController(
    IAiConnectionRepository aiConnections,
    IAiProviderDriverRegistry providerDrivers,
    ILogger<ClientAiConnectionsController> logger,
    ITenantProviderPolicyProvider providerPolicies,
    EgressUrlPolicy egressPolicy,
    IModelCatalogRepository? modelCatalog = null,
    IProviderConnectionContextFactory? hostContexts = null,
    IProviderAddInCapabilityGate? addInCapabilities = null,
    IProviderHttpClientFactory? providerHttp = null) : ControllerBase
{
    private const string RequestModelsPropertyName = "requestModels";

    private static readonly StringComparer ModelNameComparer = StringComparer.OrdinalIgnoreCase;

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection profile {ConnectionId} created for client {ClientId}")]
    private static partial void LogConnectionCreated(ILogger logger, Guid connectionId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection profile {ConnectionId} updated for client {ClientId}")]
    private static partial void LogConnectionUpdated(ILogger logger, Guid connectionId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection profile {ConnectionId} deleted for client {ClientId}")]
    private static partial void LogConnectionDeleted(ILogger logger, Guid connectionId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection profile {ConnectionId} activated for client {ClientId}")]
    private static partial void LogConnectionActivated(ILogger logger, Guid connectionId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection profile {ConnectionId} deactivated for client {ClientId}")]
    private static partial void LogConnectionDeactivated(ILogger logger, Guid connectionId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection profile {ConnectionId} verified for client {ClientId} with status {Status}")]
    private static partial void LogConnectionVerified(ILogger logger, Guid connectionId, Guid clientId, AiVerificationStatus status);

    // What a family may do while answering for values that have not been stored: the transport and nothing
    // else. Discovery and verification are calls the family makes to the endpoint an operator entered, and a
    // family reaches the network only through the factory the host supplies.
    private IProviderConnectionContext? ProbeContext()
    {
        return providerHttp is null ? null : new ProviderProbeContext(providerHttp);
    }

    private IActionResult? AuthorizeClientAccessAsync(Guid clientId)
    {
        return AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
    }

    /// <summary>Lists all AI connection profiles for the specified client.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AiConnectionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAiConnections(Guid clientId, CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        return this.Ok(await aiConnections.GetByClientAsync(clientId, ct));
    }

    /// <summary>
    ///     Lists the provider families this client can actually configure: those its tenant permits, intersected
    ///     with those this build has a driver for. An unrestricted tenant reports every implemented family rather
    ///     than an empty list, because "no restriction" and "nothing permitted" would otherwise be
    ///     indistinguishable to a caller.
    /// </summary>
    /// <remarks>
    ///     The driver intersection is what keeps the provider enum safe to open ahead of its drivers: a family
    ///     that cannot be called is never offered, so nobody configures a profile that fails at review time.
    /// </remarks>
    [HttpGet("permitted-providers")]
    [ProducesResponseType(typeof(PermittedProvidersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPermittedProviders(Guid clientId, CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        var policy = await providerPolicies.GetForClientAsync(clientId, ct);

        var providers = providerDrivers.RegisteredKinds
            .Select(providerDrivers.GetRequired)
            .Select(driver => PermittedProviderDescriptor.Describe(driver, policy.IsAllowed(driver.Declaration.Key)))
            .ToList();

        return this.Ok(new PermittedProvidersResponse(providers, policy.IsRestricted));
    }

    /// <summary>Creates a new AI connection profile for the specified client.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAiConnection(
        Guid clientId,
        [FromBody] CreateAiConnectionRequest request,
        CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        if (this.RefuseUnimplementedProvider(request.ProviderKind) is { } unimplemented)
        {
            return unimplemented;
        }

        if (await this.RefuseUnlicensedProviderAsync(request.ProviderKind, ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var writeRequest = this.TryBuildWriteRequest(request);
        if (writeRequest is null)
        {
            return this.ValidationProblem();
        }

        try
        {
            var connection = await aiConnections.AddAsync(clientId, writeRequest, ct);
            LogConnectionCreated(logger, connection.Id, clientId);
            return this.CreatedAtAction(nameof(this.GetAiConnections), new { clientId }, connection);
        }
        catch (ProviderKindNotPermittedException ex)
        {
            // A tenant policy refusal is a bad request rather than a server fault: the operator can fix it by
            // choosing a permitted provider, and the message says which ones those are.
            return this.BadRequest(new { error = ex.Message });
        }
        catch (ProviderDeclaredValueRefusedException ex)
        {
            this.ModelState.AddModelError($"providerSettings.{ex.FieldName}", ex.Reason);
            return this.ValidationProblem();
        }
    }

    /// <summary>Updates an existing AI connection profile for the specified client.</summary>
    [HttpPatch("{connectionId:guid}")]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAiConnection(
        Guid clientId,
        Guid connectionId,
        [FromBody] UpdateAiConnectionRequest request,
        CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        var existing = await aiConnections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.ClientId != clientId)
        {
            return this.NotFound();
        }

        // An update can switch the family, so the same refusals apply here as on create.
        var writtenKind = request.ProviderKind ?? existing.ProviderKind;
        if (request.ProviderKind is { } switchedKind && this.RefuseUnimplementedProvider(switchedKind) is { } unimplemented)
        {
            return unimplemented;
        }

        if (await this.RefuseUnlicensedProviderAsync(writtenKind, ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var writeRequest = this.TryBuildWriteRequest(existing, request);
        if (writeRequest is null)
        {
            return this.ValidationProblem();
        }

        try
        {
            if (!await aiConnections.UpdateAsync(connectionId, writeRequest, ct))
            {
                return this.NotFound();
            }
        }
        catch (ProviderKindNotPermittedException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (ProviderRepointNotClearedException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (ProviderDeclaredValueRefusedException ex)
        {
            this.ModelState.AddModelError($"providerSettings.{ex.FieldName}", ex.Reason);
            return this.ValidationProblem();
        }

        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        LogConnectionUpdated(logger, connectionId, clientId);
        return this.Ok(refreshed);
    }

    /// <summary>Deletes an AI connection profile.</summary>
    [HttpDelete("{connectionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAiConnection(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        var existing = await aiConnections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.ClientId != clientId)
        {
            return this.NotFound();
        }

        await aiConnections.DeleteAsync(connectionId, ct);
        LogConnectionDeleted(logger, connectionId, clientId);
        return this.NoContent();
    }

    /// <summary>Activates a verified AI connection profile after validating the minimum runtime bindings.</summary>
    [HttpPost("{connectionId:guid}/activate")]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateAiConnection(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        var existing = await aiConnections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.ClientId != clientId)
        {
            return this.NotFound();
        }

        var activation = await aiConnections.ActivateAsync(connectionId, ct);
        if (!activation.Activated)
        {
            // The reason comes from the rule that refused, so the operator is told which requirement to fix
            // rather than the full list of everything activation needs.
            return this.BadRequest(new { error = $"This profile cannot be activated: {activation.Reason}." });
        }

        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        LogConnectionActivated(logger, connectionId, clientId);
        return this.Ok(refreshed);
    }

    /// <summary>Deactivates an AI connection profile.</summary>
    [HttpPost("{connectionId:guid}/deactivate")]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateAiConnection(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        var existing = await aiConnections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.ClientId != clientId)
        {
            return this.NotFound();
        }

        await aiConnections.DeactivateAsync(connectionId, ct);
        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        LogConnectionDeactivated(logger, connectionId, clientId);
        return this.Ok(refreshed);
    }

    /// <summary>Verifies the saved provider profile and updates its verification snapshot.</summary>
    [HttpPost("{connectionId:guid}/verify")]
    [ProducesResponseType(typeof(AiVerificationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyAiConnection(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        var existing = await aiConnections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.ClientId != clientId)
        {
            return this.NotFound();
        }

        // Where the traffic goes is read first, because the refusal names the permitted hosts and the profile's
        // availability carries the same fact without them. A tenant that added or tightened its host list after
        // this profile was saved would otherwise have the stored credential sent to a host it no longer permits.
        var policy = await providerPolicies.GetForClientAsync(clientId, ct);
        var reachRefusal = policy.DescribeReachRefusal(
            existing.BaseUrl,
            providerDrivers.ReachedHostPatterns(existing.ProviderKind));
        if (reachRefusal is not null)
        {
            this.ModelState.AddModelError("baseUrl", $"This profile cannot be verified because {reachRefusal}.");
            return this.ValidationProblem();
        }

        // A profile the host cannot serve is refused before a driver is resolved for it, so a stored value the
        // profile's family does not claim is reported rather than sent: an authentication mode belongs to the family
        // that declared it, and reaching a provider with one of another family's is how a credential is
        // presented as something it is not.
        if (existing.Availability.State == AiConnectionAvailabilityState.Unavailable
            || !providerDrivers.IsRegistered(existing.ProviderKind))
        {
            this.ModelState.AddModelError(
                "providerKind",
                $"This profile cannot be verified because {DescribeUnavailability(existing)}.");
            return this.ValidationProblem();
        }

        // Verification sends the stored credential to the family, which is a use of the family, so it needs the
        // same entitlement the save needed. An installation whose licence lapsed after the profile was saved
        // would otherwise keep verifying it.
        if (await this.RefuseUnlicensedProviderAsync(existing.ProviderKind, ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var driver = providerDrivers.GetRequired(existing.ProviderKind);

        this.RefuseUndeclaredAuthMode(existing.ProviderKind, existing.AuthMode, refusedAction: "verified");
        if (!this.ModelState.IsValid)
        {
            return this.ValidationProblem();
        }

        // Re-validate the stored target before probing: a row saved before this guard existed (or saved in a
        // Development environment) could still carry a target the provider driver now rejects.
        var targetError = driver.ValidateProbeTarget(this.ProbeTarget(existing.BaseUrl, existing.AuthMode, !string.IsNullOrWhiteSpace(existing.Secret)));
        if (targetError is not null)
        {
            this.ModelState.AddModelError("baseUrl", targetError);
            return this.ValidationProblem();
        }

        // The addresses the family declared are checked again for the same reason. They are stored values a
        // family dials during its own verification, and the installation's egress rules can have tightened since
        // the profile was saved.
        if (this.RefuseStoredDeclaredEgress(driver, existing) is { } refusedEgress)
        {
            return refusedEgress;
        }

        // The stored credential is checked against the declaration for the same reason the target is: a profile
        // holding fields the family does not read, or missing ones it does, fails at the provider with a message
        // that names neither. A credential of one field comes back under the generic name, so it is read under
        // the name the stored mode declares before it is checked.
        if (!this.AcceptCredentialFields(
                existing.ProviderKind,
                existing.AuthMode,
                AiCredentialFieldSupport.Adopt(
                    driver.Declaration.CredentialFieldsFor(existing.AuthMode),
                    ProviderCredentialValues.From(ProviderSecretEnvelope.Decode(existing.Secret, existing.AuthMode).Fields))))
        {
            return this.ValidationProblem();
        }

        // Verification reflects connectivity and configured-model reachability only. Whether the product's
        // purposes are satisfied is a client-level concern resolved through logical models and the purpose map,
        // not a per-connection binding requirement.
        // Verifying is the family's own call to the provider, so the endpoint carries the primitives it reaches
        // the network through. A family with no other route to an HTTP client has none without this. Where the
        // connection-bound set cannot be built the transport alone stands in, because verification is a call to
        // the endpoint and not an action against the stored connection: without it a family loaded from a
        // directory would report a configuration it could not reach as though it had checked it.
        var verification = ProviderOutcomeGuard.Adopt(
            await driver.VerifyAsync(
                existing.ToProviderEndpoint(hostContexts?.ForConnection(existing) ?? this.ProbeContext()),
                ct),
            ProviderOutcomeGuard.SecretsOf(existing));

        await aiConnections.SaveVerificationAsync(connectionId, verification, ct);
        LogConnectionVerified(logger, connectionId, clientId, verification.Status);
        return this.Ok(verification);
    }

    /// <summary>Discovers provider models using the supplied unsaved profile settings.</summary>
    [HttpPost("discover-models")]
    [ProducesResponseType(typeof(AiModelDiscoveryResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DiscoverModels(
        Guid clientId,
        [FromBody] DiscoverModelsRequest request,
        CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        if (this.RefuseUnimplementedProvider(request.ProviderKind) is { } unimplemented)
        {
            return unimplemented;
        }

        // Discovery dials the provider with the supplied credential, so it answers the tenant's policy on the
        // same terms probing does. Without this the policy is bypassed by choosing this endpoint instead.
        if (await this.RefuseByTenantPolicyAsync(clientId, request.ProviderKind, request.BaseUrl, "used to discover models", ct)
            is { } refusedByPolicy)
        {
            return refusedByPolicy;
        }

        // The licence is answered on the same terms as create, update and verify. Discovery and probing dial the
        // family with a supplied credential, so a capability the installation is not licensed for would be
        // exercised here without ever saving a connection.
        if (await this.RefuseUnlicensedProviderAsync(request.ProviderKind, ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var probeOptions = this.TryBuildProbeOptions(request.ProviderKind, request.BaseUrl, request.Auth, request.DefaultHeaders, request.DefaultQueryParams);
        if (probeOptions is null)
        {
            return this.ValidationProblem();
        }

        var declared = this.CollectDeclaredValues(request.ProviderKind, request.ProviderSettings, existing: null);
        if (!this.ModelState.IsValid)
        {
            return this.ValidationProblem();
        }

        var driver = providerDrivers.GetRequired(request.ProviderKind);
        var discovered = ProviderOutcomeGuard.Adopt(
            await driver.DiscoverModelsAsync(
                probeOptions.ToProviderEndpoint(ProbeContext(), DeclaredValuesOf(declared)),
                ct),
            DateTimeOffset.UtcNow,
            ProviderOutcomeGuard.SecretsOf(probeOptions, declared.Secrets));

        // A model list is identifiers, not economics — several providers return nothing but an id — so the
        // catalog supplies price, context and capabilities. Without it a discovered model arrives unpriced and a
        // budget cap is enforced against zero.
        if (modelCatalog is not null)
        {
            discovered = DiscoveredModelCatalogEnricher.Enrich(
                discovered,
                await modelCatalog.GetEffectiveForClientAsync(clientId, ct: ct));
        }

        return this.Ok(discovered);
    }

    /// <summary>
    ///     Probes an unsaved profile: validates the target, then asks the provider whether the endpoint is
    ///     reachable and the credential accepted. Nothing is persisted, so a credential can be tested before it is
    ///     stored — the alternative is saving a profile in order to find out that its key is wrong.
    /// </summary>
    [HttpPost("probe")]
    [ProducesResponseType(typeof(AiVerificationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ProbeAiConnection(
        Guid clientId,
        [FromBody] ProbeAiConnectionRequest request,
        CancellationToken ct = default)
    {
        var authResult = this.AuthorizeClientAccessAsync(clientId);
        if (authResult is not null)
        {
            return authResult;
        }

        if (this.RefuseUnimplementedProvider(request.ProviderKind) is { } unimplemented)
        {
            return unimplemented;
        }

        // The tenant's provider policy is answered before anything is dialled: probing a forbidden provider would
        // reach it with a credential the tenant has decided it does not want used.
        if (await this.RefuseByTenantPolicyAsync(clientId, request.ProviderKind, request.BaseUrl, "probed", ct)
            is { } refusedByPolicy)
        {
            return refusedByPolicy;
        }

        // The licence is answered on the same terms as create, update and verify. Discovery and probing dial the
        // family with a supplied credential, so a capability the installation is not licensed for would be
        // exercised here without ever saving a connection.
        if (await this.RefuseUnlicensedProviderAsync(request.ProviderKind, ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var probeOptions = this.TryBuildProbeOptions(
            request.ProviderKind,
            request.BaseUrl,
            request.Auth,
            request.DefaultHeaders,
            request.DefaultQueryParams);
        if (probeOptions is null)
        {
            return this.ValidationProblem();
        }

        var declared = this.CollectDeclaredValues(request.ProviderKind, request.ProviderSettings, existing: null);
        if (!this.ModelState.IsValid)
        {
            return this.ValidationProblem();
        }

        var driver = providerDrivers.GetRequired(request.ProviderKind);
        return this.Ok(
            ProviderOutcomeGuard.Adopt(
                await driver.VerifyAsync(
                    probeOptions.ToProviderEndpoint(ProbeContext(), DeclaredValuesOf(declared)),
                    ct),
                ProviderOutcomeGuard.SecretsOf(probeOptions, declared.Secrets)));
    }

    // The tenant's provider policy on a route that dials a provider with operator-supplied settings. Both the
    // family and the address it would be reached at are answered, because a policy can forbid either. Routes that
    // act on a saved profile read the family half from the profile's availability, which the repository resolves
    // against the same policy.
    private async Task<IActionResult?> RefuseByTenantPolicyAsync(
        Guid clientId,
        string providerKind,
        string? baseUrl,
        string refusedAction,
        CancellationToken ct)
    {
        var policy = await providerPolicies.GetForClientAsync(clientId, ct);
        if (policy.GetRefusalReason(providerKind) is { } refusal)
        {
            this.ModelState.AddModelError("providerKind", $"This profile cannot be {refusedAction} because {refusal}.");
            return this.ValidationProblem();
        }

        if (policy.DescribeReachRefusal(baseUrl, providerDrivers.ReachedHostPatterns(providerKind)) is { } reachRefusal)
        {
            this.ModelState.AddModelError("baseUrl", $"This profile cannot be {refusedAction} because {reachRefusal}.");
            return this.ValidationProblem();
        }

        return null;
    }

    // A provider family this build cannot call is refused where the operator can see it, naming what is
    // available. Without this, opening the enum ahead of a driver would turn into a 500 from the registry.
    // The licence check on the family a connection is being written against. It sits beside the driver check
    // because both answer whether this installation can use the family at all, and an operator finds out while
    // the form is still open rather than at the first review.
    private async Task<IActionResult?> RefuseUnlicensedProviderAsync(string providerKind, CancellationToken ct)
    {
        if (await ProviderCapabilityRefusal.DescribeAsync(providerDrivers, addInCapabilities, providerKind, ct)
            is not { } refusal)
        {
            return null;
        }

        this.ModelState.AddModelError("providerKind", $"This connection cannot be saved because {refusal}.");
        return this.ValidationProblem();
    }

    // The installation's egress rules over the addresses a family declared, re-applied to what is stored. The
    // write path refuses such an address, so a stored one was permitted when it was saved; an installation that
    // has since withdrawn private egress, or a family that has since declared a field as an address, leaves a row
    // holding one the installation no longer permits, and verification is where it would next be dialled.
    private IActionResult? RefuseStoredDeclaredEgress(IAiProviderDriver driver, AiConnectionDto connection)
    {
        // Settings and declared secrets together. A family may declare a Url field as a secret, and reading
        // only the settings left that address unchecked, so an installation that tightened its egress rules
        // still dialled it. The secret values are compared and not stored anywhere by this call.
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in connection.ProviderSettings
                                      ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            declared[name] = value;
        }

        foreach (var (name, value) in connection.DeclaredSecrets)
        {
            declared[name] = value;
        }

        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(driver.Declaration, declared, egressPolicy);

        if (refusals.Count == 0)
        {
            return null;
        }

        foreach (var (field, message) in refusals)
        {
            this.ModelState.AddModelError($"providerSettings.{field}", message);
        }

        return this.ValidationProblem();
    }

    private IActionResult? RefuseUnimplementedProvider(string providerKind)
    {
        if (providerDrivers.IsRegistered(providerKind))
        {
            return null;
        }

        this.ModelState.AddModelError(
            "providerKind",
            $"This build has no driver for the '{providerKind}' provider "
            + $"(available: {string.Join(", ", providerDrivers.RegisteredKinds)}).");
        return this.ValidationProblem();
    }

    // An authentication mode the family does not declare cannot be used: the credential would be written where
    // that provider does not read it, and the failure would come back as a provider-worded rejection naming
    // neither the mode nor the family. A save is refused so the operator reads it on the form. A verification is
    // refused too, which keeps a profile stored before this rule existed out of a review, because activation
    // requires a verified connection.
    private void RefuseUndeclaredAuthMode(string providerKind, string authMode, string refusedAction)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return;
        }

        var supported = providerDrivers.GetRequired(providerKind).SupportedAuthModes;
        if (AiAuthModeSupport.GetRefusalReason(providerKind, supported, authMode) is { } refusal)
        {
            this.ModelState.AddModelError("authMode", $"This profile cannot be {refusedAction} because {refusal}.");
        }
    }

    // A binding names the protocol mode a call will use, so a shape this provider cannot speak is refused while the
    // operator is looking at the form. The driver refuses it again at call time, but by then a review is running.
    private void RefuseUnspeakableProtocols(string providerKind, IReadOnlyList<AiPurposeBindingDto> bindings)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return;
        }

        var supported = providerDrivers.GetRequired(providerKind).SupportedProtocolModes;
        foreach (var binding in bindings)
        {
            // Read against the family first, so a binding naming a spelling the family supersedes is understood.
            var requested = providerDrivers
                .ResolveProtocolMode(providerKind, binding.ProtocolMode)
                .ValueOr(binding.ProtocolMode);

            if (AiProtocolModeSupport.GetRefusalReason(providerKind, supported, requested) is { } refusal)
            {
                this.ModelState.AddModelError(
                    "purposeBindings",
                    $"The '{binding.Purpose}' binding cannot be saved because {refusal}.");
            }
        }
    }

    private AiConnectionWriteRequestDto? TryBuildWriteRequest(CreateAiConnectionRequest request)
    {
        var probeOptions = this.TryBuildProbeOptions(
            request.ProviderKind,
            request.BaseUrl,
            request.Auth,
            request.DefaultHeaders,
            request.DefaultQueryParams);

        if (probeOptions is null)
        {
            return null;
        }

        var displayName = NormalizeDisplayName(request.DisplayName);
        if (displayName is null)
        {
            this.ModelState.AddModelError(nameof(request.DisplayName), "displayName is required and must be 200 characters or fewer.");
            return null;
        }

        var configuredModels = this.NormalizeConfiguredModels(request.ProviderKind, request.ConfiguredModels);
        var purposeBindings = this.NormalizePurposeBindings(request.PurposeBindings, configuredModels);
        this.RefuseUnspeakableProtocols(request.ProviderKind, purposeBindings);
        this.RefuseUndeclaredAuthMode(request.ProviderKind, probeOptions.AuthMode, refusedAction: "saved");
        var declared = this.CollectDeclaredValues(request.ProviderKind, request.ProviderSettings, existing: null);

        if (!this.ModelState.IsValid)
        {
            return null;
        }

        return new AiConnectionWriteRequestDto(
            displayName,
            request.ProviderKind,
            probeOptions.BaseUrl,
            probeOptions.AuthMode,
            request.DiscoveryMode,
            configuredModels,
            purposeBindings,
            NormalizeMap(request.DefaultHeaders),
            NormalizeMap(request.DefaultQueryParams),
            probeOptions.Secret,
            declared.Settings,
            declared.Secrets);
    }

    private AiConnectionWriteRequestDto? TryBuildWriteRequest(AiConnectionDto existing, UpdateAiConnectionRequest request)
    {
        var providerKind = request.ProviderKind ?? existing.ProviderKind;
        var auth = this.CarryStoredCredential(existing, providerKind, request.Auth);
        var baseUrl = request.BaseUrl ?? existing.BaseUrl;
        var defaultHeaders = request.DefaultHeaders ?? existing.DefaultHeaders;
        var defaultQueryParams = request.DefaultQueryParams ?? existing.DefaultQueryParams;
        var discoveryMode = request.DiscoveryMode ?? existing.DiscoveryMode;

        var probeOptions = this.TryBuildProbeOptions(providerKind, baseUrl, auth, defaultHeaders, defaultQueryParams);
        if (probeOptions is null)
        {
            return null;
        }

        var displayName = NormalizeDisplayName(request.DisplayName ?? existing.DisplayName);
        if (displayName is null)
        {
            this.ModelState.AddModelError(nameof(request.DisplayName), "displayName is required and must be 200 characters or fewer.");
            return null;
        }

        var configuredModels = this.NormalizeConfiguredModels(
            providerKind,
            request.ConfiguredModels ?? existing.ConfiguredModels.Select(ToConfiguredModelRequest).ToList());
        var purposeBindings = this.NormalizePurposeBindings(
            request.PurposeBindings ?? existing.PurposeBindings.Select(ToBindingRequest).ToList(),
            configuredModels);
        this.RefuseUnspeakableProtocols(providerKind, purposeBindings);
        this.RefuseUndeclaredAuthMode(providerKind, probeOptions.AuthMode, refusedAction: "saved");

        // A move to another family collects against nothing stored. The departing family's values are not carried
        // across — the repository drops them — and counting them as stored would let a same-named field of the
        // family that is leaving satisfy a required field of the family arriving, saving a connection whose
        // required value is empty.
        var declared = this.CollectDeclaredValues(
            providerKind,
            request.ProviderSettings,
            providerKind == existing.ProviderKind ? existing : null);

        if (!this.ModelState.IsValid)
        {
            return null;
        }

        return new AiConnectionWriteRequestDto(
            displayName,
            providerKind,
            probeOptions.BaseUrl,
            probeOptions.AuthMode,
            discoveryMode,
            configuredModels,
            purposeBindings,
            NormalizeMap(defaultHeaders),
            NormalizeMap(defaultQueryParams),
            probeOptions.Secret,
            declared.Settings,
            declared.Secrets);
    }

    // An update that changes anything other than the credential leaves the credential boxes empty, and the
    // stored one has to survive that. It is read back as fields rather than as one string, because the stored
    // value of a multi-field credential is an envelope and carrying it forward as a key would store that
    // envelope's own text as the credential.
    private AiConnectionAuthRequest CarryStoredCredential(
        AiConnectionDto existing,
        string providerKind,
        AiConnectionAuthRequest? auth)
    {
        var requested = auth ?? new AiConnectionAuthRequest(existing.AuthMode);
        if (AiConnectionCredential.Collect(requested).Count > 0)
        {
            return requested;
        }

        var stored = ProviderSecretEnvelope.Decode(existing.Secret, existing.AuthMode).Fields;
        if (stored.Count == 0 || !providerDrivers.IsRegistered(providerKind))
        {
            return requested;
        }

        // A credential of one field is stored as its value alone, so it comes back under the generic name and is
        // adopted under the one its own mode declares — the mode it was saved under, not the one being requested.
        // Naming it after the requested mode would present the stored material as that mode's credential: a
        // Vertex profile's service-account document carried into the API-key mode would then be sent as a Gemini
        // key, putting its private key in a header. Named after the stored mode instead, a mode change without a
        // re-entered credential leaves fields the new mode does not declare, and is refused.
        var declared = providerDrivers.GetRequired(providerKind).Declaration.CredentialFieldsFor(existing.AuthMode);

        return requested with
        {
            ApiKey = null,
            Fields = AiCredentialFieldSupport.Adopt(declared, ProviderCredentialValues.From(stored)),
        };
    }

    // The values the family declared, split into the ones the host persists in the settings document and the ones
    // it puts in the credential envelope. Each refusal is keyed to the field it is about, so a console attaches it
    // to the input the operator has to fix. A family this build has no driver for declares nothing, and the save
    // is already refused for naming a family that cannot be called.
    private DeclaredValueSubmission CollectDeclaredValues(
        string providerKind,
        IReadOnlyDictionary<string, string>? submitted,
        AiConnectionDto? existing)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return new DeclaredValueSubmission(new Dictionary<string, string>(), new Dictionary<string, string>(), []);
        }

        var driver = providerDrivers.GetRequired(providerKind);
        var submission = AiConnectionDeclaredValues.Collect(
            driver.Declaration,
            submitted,
            existing?.ProviderSettings,
            existing?.DeclaredSecretNames);

        foreach (var (field, message) in submission.Refusals)
        {
            this.ModelState.AddModelError($"providerSettings.{field}", message);
        }

        foreach (var (field, message) in DeclaredValueValidation.Refusals(driver, submission, egressPolicy))
        {
            this.ModelState.AddModelError($"providerSettings.{field}", message);
        }

        return submission;
    }

    // The family declared one set of fields and reads back one set of values, so the two halves the host keeps
    // apart are put back together here the way the saved-connection overload puts them back together.
    private static IReadOnlyDictionary<string, string> DeclaredValuesOf(DeclaredValueSubmission submission)
    {
        var merged = new Dictionary<string, string>(submission.Settings, StringComparer.Ordinal);
        foreach (var (name, value) in submission.Secrets)
        {
            merged[name] = value;
        }

        return merged;
    }

    private AiConnectionProbeOptionsDto? TryBuildProbeOptions(
        string providerKind,
        string? baseUrl,
        AiConnectionAuthRequest? auth,
        IReadOnlyDictionary<string, string>? defaultHeaders,
        IReadOnlyDictionary<string, string>? defaultQueryParams)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 1000 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            this.ModelState.AddModelError(nameof(baseUrl), "baseUrl is required, must be an absolute URL, and must be 1000 characters or fewer.");
            return null;
        }

        if (auth is null)
        {
            this.ModelState.AddModelError(nameof(auth), "auth is required.");
            return null;
        }

        // The installation's own egress rules come first, so a family cannot admit an address this installation
        // refuses. A family may still refuse more, which the driver check below is for.
        if (egressPolicy.GetRefusalReason(baseUrl.Trim(), "baseUrl") is { } egressRefusal)
        {
            this.ModelState.AddModelError(nameof(baseUrl), egressRefusal);
            return null;
        }

        var driver = providerDrivers.GetRequired(providerKind);
        var fields = AiConnectionCredential.Collect(auth);

        // An authentication mode is read against the family before anything else uses it, so a caller naming the
        // spelling this family supersedes is understood and the value is stored under the name the family
        // declares now. A shape nothing claims is carried as it was sent, and refused below by name.
        var authMode = providerDrivers.ResolveAuthMode(providerKind, auth.Mode).ValueOr(auth.Mode?.Trim() ?? string.Empty);

        // The size bound on what is written into the protected column. It is checked before the fields are read
        // against the declaration so an oversized value is named as oversized rather than as the wrong shape.
        foreach (var refusal in AiConnectionCredential.FindCredentialSizeRefusals(fields))
        {
            this.ModelState.AddModelError("auth", refusal);
        }

        if (!this.ModelState.IsValid)
        {
            return null;
        }

        // Provider-specific base-URL / SSRF-egress / auth-shape validation lives behind the driver seam,
        // so the controller does not branch on provider kind. It runs before the fields are checked against the
        // declaration: a mode that does not fit the endpoint is the more useful thing to report, and reporting
        // the fields of a mode that cannot be used here would send the operator to fill in the wrong ones.
        var targetError = driver.ValidateProbeTarget(this.ProbeTarget(baseUrl.Trim(), authMode, fields.Count > 0));
        if (targetError is not null)
        {
            this.ModelState.AddModelError(nameof(baseUrl), targetError);
            return null;
        }

        // The field check reports nothing for a mode the family does not declare, because the mode is the actual
        // problem and this is what names it. Probing ran without it, so a mode refused when the same profile is
        // saved or verified was accepted here.
        this.RefuseUndeclaredAuthMode(providerKind, authMode, refusedAction: "probed");
        if (!this.ModelState.IsValid)
        {
            return null;
        }

        if (!this.AcceptCredentialFields(providerKind, authMode, ProviderCredentialValues.From(fields)))
        {
            return null;
        }

        return new AiConnectionProbeOptionsDto(
            providerKind,
            baseUrl.Trim(),
            authMode,
            AiConnectionCredential.Encode(authMode, fields),
            NormalizeMap(defaultHeaders),
            NormalizeMap(defaultQueryParams));
    }

    // What a family is asked to validate, carrying what this installation permits an address to reach. A family
    // compiled into the host reads those settings where it is composed; one loaded from a directory is
    // constructed with no arguments, so the target is where it learns them. The installation's own check above
    // has already run, so this widens nothing: it only keeps a family from refusing what the host admitted.
    private AiProbeTarget ProbeTarget(string baseUrl, string authMode, bool hasApiKey)
    {
        return new AiProbeTarget(baseUrl, authMode, hasApiKey)
        {
            AllowsPrivateAddress = egressPolicy.AllowPrivateEgress,
            AllowsInsecureScheme = egressPolicy.AllowInsecureScheme,
        };
    }

    // A credential is only usable if it is the one the family reads: a required field left empty produces a
    // profile that fails on its first call, and a field the family never declared is a value the operator
    // believes is in use and nothing reads. Both are reported while the form is still open, naming the field.
    // Worded from the profile's own availability so the console, a review and this endpoint all name the same
    // remedy for the same profile.
    private static string DescribeUnavailability(AiConnectionDto connection)
    {
        var identity = string.IsNullOrWhiteSpace(connection.Availability.ProviderIdentity)
            ? connection.ProviderKind.ToString()
            : connection.Availability.ProviderIdentity;

        return connection.Availability.Reason switch
        {
            AiConnectionUnavailableReason.ProviderFamilyNotPermitted =>
                $"the '{identity}' provider is not permitted for this tenant",

            // Reached only by a caller that did not read the endpoint list itself. The verify route does, above,
            // and its refusal names the permitted hosts.
            AiConnectionUnavailableReason.EndpointNotPermitted =>
                "this tenant does not permit where its traffic would go",

            // The values are named, because "a stored value" leaves an operator opening the profile to find out
            // which one, and the set of valid values is what the loaded families declare rather than a list a
            // console could check against.
            AiConnectionUnavailableReason.StoredValueUnresolved =>
                $"it holds stored value(s) the '{identity}' provider does not claim: "
                + string.Join(
                    ", ",
                    connection.Availability.UnresolvedValues.Select(value => $"'{value.Value}'")),
            _ => $"this build has no driver for the '{identity}' provider",
        };
    }

    private bool AcceptCredentialFields(
        string providerKind,
        string authMode,
        ProviderCredentialValues fields)
    {
        var driver = providerDrivers.GetRequired(providerKind);
        if (!driver.CredentialFields.ContainsKey(authMode))
        {
            // A mode the family does not declare has no fields either, so every supplied value would be reported
            // as unknown. The mode is the actual problem and is refused as such, naming it and the family.
            return true;
        }

        var refusals = AiCredentialFieldSupport.FindCredentialFieldRefusals(
            providerKind,
            authMode,
            driver.Declaration.CredentialFieldsFor(authMode),
            fields);

        foreach (var refusal in refusals)
        {
            this.ModelState.AddModelError("auth", refusal);
        }

        return refusals.Count == 0;
    }

    private IReadOnlyList<AiConfiguredModelDto> NormalizeConfiguredModels(
        string providerKind,
        IReadOnlyList<AiConfiguredModelRequest>? requestModels)
    {
        if (requestModels is null || requestModels.Count == 0)
        {
            this.ModelState.AddModelError(nameof(requestModels), "configuredModels must contain at least one model.");
            return [];
        }

        var models = new List<AiConfiguredModelDto>();
        var seen = new HashSet<string>(ModelNameComparer);

        foreach (var requestModel in requestModels)
        {
            var normalizedModel = this.NormalizeConfiguredModel(providerKind, requestModel, seen);
            if (normalizedModel is not null)
            {
                models.Add(normalizedModel);
            }
        }

        return models.AsReadOnly();
    }

    private AiConfiguredModelDto? NormalizeConfiguredModel(
        string providerKind,
        AiConfiguredModelRequest requestModel,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(requestModel.RemoteModelId))
        {
            this.ModelState.AddModelError(RequestModelsPropertyName, "Each configured model requires remoteModelId.");
            return null;
        }

        var remoteModelId = requestModel.RemoteModelId.Trim();
        if (!seen.Add(remoteModelId))
        {
            this.ModelState.AddModelError(RequestModelsPropertyName, $"Configured model '{remoteModelId}' is duplicated.");
            return null;
        }

        var inferredEmbedding = IsEmbeddingModel(remoteModelId, requestModel);
        var defaultOperationKinds = inferredEmbedding
            ? new List<AiOperationKind> { AiOperationKind.Embedding }.AsReadOnly()
            : new List<AiOperationKind> { AiOperationKind.Chat }.AsReadOnly();
        var operationKinds = requestModel.OperationKinds is { Count: > 0 }
            ? requestModel.OperationKinds.Distinct().ToList().AsReadOnly()
            : defaultOperationKinds;

        var isEmbeddingOnly = operationKinds.Contains(AiOperationKind.Embedding) && !operationKinds.Contains(AiOperationKind.Chat);
        var protocolModes = requestModel.SupportedProtocolModes is { Count: > 0 }
            ? requestModel.SupportedProtocolModes.Distinct(ProviderVocabulary.ValueComparer).ToList().AsReadOnly()
            : this.DefaultProtocolModes(providerKind, isEmbeddingOnly);

        if (operationKinds.Contains(AiOperationKind.Embedding))
        {
            this.AddEmbeddingModelErrors(requestModel, remoteModelId);
        }

        if (ProviderVocabulary.Names(protocolModes, ProviderDeclaredProtocolModes.Embeddings)
            && !operationKinds.Contains(AiOperationKind.Embedding))
        {
            this.ModelState.AddModelError(
                RequestModelsPropertyName,
                $"Model '{remoteModelId}' cannot declare the embeddings protocol without embedding capability.");
        }

        // Every shape a family owns is a chat shape: the two the host reserves are the automatic one and the
        // embeddings one, and the host cannot read a family's own names to tell them apart further.
        if (protocolModes.Any(mode => !ProviderDeclaredProtocolModes.IsReserved(mode))
            && !operationKinds.Contains(AiOperationKind.Chat))
        {
            this.ModelState.AddModelError(RequestModelsPropertyName, $"Model '{remoteModelId}' cannot declare chat protocols without chat capability.");
        }

        return new AiConfiguredModelDto(
            requestModel.Id ?? Guid.Empty,
            remoteModelId,
            string.IsNullOrWhiteSpace(requestModel.DisplayName) ? remoteModelId : requestModel.DisplayName.Trim(),
            operationKinds,
            protocolModes,
            string.IsNullOrWhiteSpace(requestModel.TokenizerName) ? null : requestModel.TokenizerName.Trim(),
            requestModel.MaxInputTokens,
            requestModel.EmbeddingDimensions,
            requestModel.SupportsStructuredOutput,
            requestModel.SupportsToolUse,
            requestModel.Source ?? AiConfiguredModelSource.Manual,
            requestModel.LastSeenAt,
            requestModel.InputCostPer1MUsd,
            requestModel.OutputCostPer1MUsd,
            requestModel.MaxContextTokens,
            requestModel.CachedInputCostPer1MUsd,
            requestModel.CacheWriteCostPer1MUsd,
            requestModel.SupportsReasoning,
            requestModel.SupportsPromptCaching,
            string.IsNullOrWhiteSpace(requestModel.ReasoningContentField) ? null : requestModel.ReasoningContentField.Trim());
    }

    private void AddEmbeddingModelErrors(AiConfiguredModelRequest requestModel, string remoteModelId)
    {
        if (string.IsNullOrWhiteSpace(requestModel.TokenizerName))
        {
            this.ModelState.AddModelError(RequestModelsPropertyName, $"Embedding model '{remoteModelId}' requires tokenizerName.");
        }

        if (!requestModel.MaxInputTokens.HasValue || requestModel.MaxInputTokens.Value <= 0)
        {
            this.ModelState.AddModelError(RequestModelsPropertyName, $"Embedding model '{remoteModelId}' requires maxInputTokens greater than zero.");
        }

        if (!requestModel.EmbeddingDimensions.HasValue || requestModel.EmbeddingDimensions.Value is < 64 or > 4096)
        {
            this.ModelState.AddModelError(
                RequestModelsPropertyName,
                $"Embedding model '{remoteModelId}' requires embeddingDimensions between 64 and 4096.");
        }
    }

    private IReadOnlyList<AiPurposeBindingDto> NormalizePurposeBindings(
        IReadOnlyList<AiPurposeBindingRequest>? requestBindings,
        IReadOnlyList<AiConfiguredModelDto> configuredModels)
    {
        // A profile with no purpose bindings is legitimate: a client that selects its models through logical
        // models never binds a purpose to a connection directly, so requiring one here made every such profile
        // permanently unsavable. Editing anything on it, including a model's pricing, was refused with a message
        // about bindings the operator had deliberately not created. Whether a profile is usable is decided at
        // activation, which has its own rules, rather than by forbidding the shape at rest.
        if (requestBindings is null || requestBindings.Count == 0)
        {
            return [];
        }

        var modelsById = configuredModels
            .Where(model => model.Id != Guid.Empty)
            .ToDictionary(model => model.Id);
        var modelsByRemoteModelId = configuredModels.ToDictionary(model => model.RemoteModelId, ModelNameComparer);
        var bindings = new List<AiPurposeBindingDto>();
        var seenPurposes = new HashSet<AiPurpose>();

        foreach (var requestBinding in requestBindings)
        {
            var binding = this.NormalizePurposeBinding(requestBinding, modelsById, modelsByRemoteModelId, seenPurposes);
            if (binding is not null)
            {
                bindings.Add(binding);
            }
        }

        return bindings.AsReadOnly();
    }

    private AiPurposeBindingDto? NormalizePurposeBinding(
        AiPurposeBindingRequest requestBinding,
        IReadOnlyDictionary<Guid, AiConfiguredModelDto> modelsById,
        IReadOnlyDictionary<string, AiConfiguredModelDto> modelsByRemoteModelId,
        HashSet<AiPurpose> seenPurposes)
    {
        const string RequestBindingsPropertyName = "requestBindings";

        if (!seenPurposes.Add(requestBinding.Purpose))
        {
            this.ModelState.AddModelError(RequestBindingsPropertyName, $"Purpose '{requestBinding.Purpose}' is duplicated.");
            return null;
        }

        if (!requestBinding.IsEnabled &&
            (!requestBinding.ConfiguredModelId.HasValue || requestBinding.ConfiguredModelId.Value == Guid.Empty) &&
            string.IsNullOrWhiteSpace(requestBinding.RemoteModelId))
        {
            return null;
        }

        var model = ResolveConfiguredModel(requestBinding, modelsById, modelsByRemoteModelId);
        if (model is null)
        {
            this.ModelState.AddModelError(RequestBindingsPropertyName, $"Purpose '{requestBinding.Purpose}' references an unknown configured model.");
            return null;
        }

        this.AddPurposeBindingCapabilityErrors(requestBinding, model);

        return new AiPurposeBindingDto(
            requestBinding.Id ?? Guid.Empty,
            requestBinding.Purpose,
            model.Id == Guid.Empty ? null : model.Id,
            model.RemoteModelId,
            requestBinding.ProtocolMode,
            requestBinding.IsEnabled);
    }

    private static AiConfiguredModelDto? ResolveConfiguredModel(
        AiPurposeBindingRequest requestBinding,
        IReadOnlyDictionary<Guid, AiConfiguredModelDto> modelsById,
        IReadOnlyDictionary<string, AiConfiguredModelDto> modelsByRemoteModelId)
    {
        AiConfiguredModelDto? model = null;
        if (requestBinding.ConfiguredModelId.HasValue && requestBinding.ConfiguredModelId.Value != Guid.Empty)
        {
            modelsById.TryGetValue(requestBinding.ConfiguredModelId.Value, out model);
        }

        if (model is null && !string.IsNullOrWhiteSpace(requestBinding.RemoteModelId))
        {
            modelsByRemoteModelId.TryGetValue(requestBinding.RemoteModelId.Trim(), out model);
        }

        return model;
    }

    private void AddPurposeBindingCapabilityErrors(AiPurposeBindingRequest requestBinding, AiConfiguredModelDto model)
    {
        const string RequestBindingsPropertyName = "requestBindings";

        if (requestBinding.Purpose == AiPurpose.EmbeddingDefault)
        {
            if (!model.SupportsEmbedding)
            {
                this.ModelState.AddModelError(RequestBindingsPropertyName, $"Purpose '{requestBinding.Purpose}' requires an embedding-capable model.");
            }

            if (!ProviderVocabulary.Names(
                    [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
                    requestBinding.ProtocolMode))
            {
                this.ModelState.AddModelError(
                    RequestBindingsPropertyName,
                    $"Purpose '{requestBinding.Purpose}' must use the embeddings protocol or automatic mode.");
            }

            return;
        }

        if (!model.SupportsChat)
        {
            this.ModelState.AddModelError(RequestBindingsPropertyName, $"Purpose '{requestBinding.Purpose}' requires a chat-capable model.");
        }

        if (!ProviderVocabulary.ValuesEqual(requestBinding.ProtocolMode, ProviderDeclaredProtocolModes.Auto)
            && !ProviderVocabulary.Names(model.SupportedProtocolModes, requestBinding.ProtocolMode))
        {
            this.ModelState.AddModelError(
                RequestBindingsPropertyName,
                $"Model '{model.RemoteModelId}' does not support protocol '{requestBinding.ProtocolMode}'.");
        }
    }

    // The shapes a model serves when the request states none. An embedding-only model gets the two the host
    // reserves; anything else gets what the family declares it speaks, without the embeddings shape. A family
    // this build has no driver for declares nothing, so the automatic shape stands alone, which it
    // means: the driver picks the wire format.
    private IReadOnlyList<string> DefaultProtocolModes(string providerKind, bool isEmbeddingOnly)
    {
        if (isEmbeddingOnly)
        {
            return [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings];
        }

        var declared = providerDrivers.IsRegistered(providerKind)
            ? providerDrivers.GetRequired(providerKind).SupportedProtocolModes
            : [ProviderDeclaredProtocolModes.Auto];

        return
        [
            .. declared.Where(mode =>
                !ProviderVocabulary.ValuesEqual(mode, ProviderDeclaredProtocolModes.Embeddings)),
        ];
    }

    private static bool IsEmbeddingModel(string remoteModelId, AiConfiguredModelRequest requestModel)
    {
        return remoteModelId.Contains("embedding", StringComparison.OrdinalIgnoreCase)
               || !string.IsNullOrWhiteSpace(requestModel.TokenizerName)
               || requestModel.EmbeddingDimensions.HasValue;
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        return string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200
            ? null
            : displayName.Trim();
    }

    private static Dictionary<string, string> NormalizeMap(IReadOnlyDictionary<string, string>? source)
    {
        return source is null
            ? []
            : source
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.First().Key.Trim(), group => group.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Restates a stored model as the request that would produce it, for an update that names no models of its
    ///     own. Every field the model carries has to survive the trip: a value dropped here is not merely absent
    ///     from the request, it is written back as null over what was stored.
    /// </summary>
    private static AiConfiguredModelRequest ToConfiguredModelRequest(AiConfiguredModelDto model)
    {
        return new AiConfiguredModelRequest(
            model.Id,
            model.RemoteModelId,
            model.DisplayName,
            model.OperationKinds,
            model.SupportedProtocolModes,
            model.TokenizerName,
            model.MaxInputTokens,
            model.EmbeddingDimensions,
            model.SupportsStructuredOutput,
            model.SupportsToolUse,
            model.Source,
            model.LastSeenAt,
            model.InputCostPer1MUsd,
            model.OutputCostPer1MUsd,
            model.MaxContextTokens,
            model.CachedInputCostPer1MUsd,
            model.CacheWriteCostPer1MUsd,
            model.SupportsReasoning,
            model.SupportsPromptCaching,
            model.ReasoningContentField);
    }

    private static AiPurposeBindingRequest ToBindingRequest(AiPurposeBindingDto binding)
    {
        return new AiPurposeBindingRequest(
            binding.Id,
            binding.Purpose,
            binding.ConfiguredModelId,
            binding.RemoteModelId,
            binding.ProtocolMode,
            binding.IsEnabled);
    }
}

/// <summary>Request body for probing a profile that has not been saved yet.</summary>
/// <param name="ProviderKind">The provider family to probe.</param>
/// <param name="BaseUrl">The base URL to probe.</param>
/// <param name="Auth">The credential to probe with; never stored by this call.</param>
/// <param name="DefaultHeaders">Optional headers the profile would send.</param>
/// <param name="DefaultQueryParams">Optional query parameters the profile would send.</param>
/// <param name="ProviderSettings">
///     The family's declared settings as the form holds them. Carried so a family whose verification reads one of
///     them is probed against the configuration the saved connection would have.
/// </param>
public sealed record ProbeAiConnectionRequest(
    [property: JsonRequired] string ProviderKind,
    string BaseUrl,
    AiConnectionAuthRequest Auth,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    IReadOnlyDictionary<string, string>? ProviderSettings = null)
{
    /// <summary>Renders the request without the key; see <see cref="CreateAiConnectionRequest.ToString" />.</summary>
    public override string ToString()
    {
        return
            $"{nameof(ProbeAiConnectionRequest)} {{ ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, "
            + $"Auth = {this.Auth}, DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
            + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}] }}";
    }
}

/// <summary>One provider family this build can call, and what a given client may do with it.</summary>
/// <param name="ProviderKind">The provider family.</param>
/// <param name="IsPermitted">Whether the client's tenant permits it.</param>
/// <param name="Label">
///     What an operator sees where the family is offered. Sent because the family declares it: a console holding
///     its own catalogue of names shows a key instead of a name for every family installed after it shipped.
/// </param>
/// <param name="ProtocolModes">
///     The protocol modes this provider's driver can speak, each with the name an operator sees. Sent so the
///     configuration UI offers only shapes that can actually be called, rather than keeping a second copy of the
///     drivers' knowledge.
/// </param>
/// <param name="AuthModes">
///     The authentication modes this provider's driver can authenticate with, each with the name an operator sees.
///     Sent for the same reason as the protocol modes. Families differ here — an Azure resource takes a managed
///     identity, Bedrock signs with an access key, Anthropic reads an <c>x-api-key</c> header — and a UI holding
///     its own copy of that offers modes the family cannot read.
/// </param>
/// <param name="CredentialFields">
///     The fields each of those authentication modes needs, keyed by mode. A credential is one key for most
///     families and several values for some — an access key id and a secret access key, a service-account
///     document — so a form that assumed one input could not configure them at all. Sent with the modes because
///     the two are answered together: choosing a mode is choosing which of these to fill in.
/// </param>
/// <param name="DeclaredFields">
///     The configuration fields this family declares, so the form for it can be rendered before a connection
///     exists. Empty for a family that declares none, which is every family compiled into this build: their
///     address, credential and verification state are columns of their own.
/// </param>
/// <param name="ConnectionForm">
///     What the family says about the connection values the host collects for every family — the display name,
///     the base URL and the default query parameters. Null for a family that says nothing about them, which
///     leaves the console's own family-neutral text in place.
/// </param>
public sealed record PermittedProviderDescriptor(
    string ProviderKind,
    bool IsPermitted,
    string Label,
    IReadOnlyList<AiProtocolModeOptionDto> ProtocolModes,
    IReadOnlyList<AiAuthModeOptionDto> AuthModes,
    IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields,
    IReadOnlyList<AiDeclaredFieldDto> DeclaredFields,
    AiProviderConnectionFormDto? ConnectionForm)
{
    /// <summary>Describes one registered driver for a console.</summary>
    /// <param name="driver">The driver to describe.</param>
    /// <param name="isPermitted">Whether the tenant this is answered for permits the family.</param>
    /// <remarks>
    ///     Described without a connection, so there are no stored credential values to scrub against; the cap
    ///     still applies to every string, because all of them were written by the family.
    /// </remarks>
    public static PermittedProviderDescriptor Describe(IAiProviderDriver driver, bool isPermitted)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return new PermittedProviderDescriptor(
            driver.Declaration.Key,
            isPermitted,
            ProviderDeclarationProjection.Label(driver.Declaration),
            ProviderDeclarationProjection.ProtocolModes(driver.Declaration),
            ProviderDeclarationProjection.AuthModes(driver.Declaration),
            driver.CredentialFields,
            ProviderDeclaredFieldProjection.Describe(driver.Declaration),
            ProviderDeclarationProjection.ConnectionForm(driver.Declaration));
    }
}

/// <summary>What a client may configure, and enough to explain anything it may not.</summary>
/// <param name="Providers">
///     Every family this build has a driver for, each flagged with whether the tenant permits it. A family absent
///     from this list has no driver at all — the two reasons for unavailability are different and need different
///     fixes, so they are reported apart rather than collapsed into one refusal.
/// </param>
/// <param name="IsRestricted">Whether the tenant has stated a provider policy at all.</param>
public sealed record PermittedProvidersResponse(
    IReadOnlyList<PermittedProviderDescriptor> Providers,
    bool IsRestricted);

/// <summary>Authentication settings for one AI connection profile request.</summary>
/// <param name="Mode">The authentication mode the credential belongs to.</param>
/// <param name="ApiKey">
///     The credential of a mode whose credential is one key. It means the <c>apiKey</c> field of
///     <paramref name="Fields" /> and is kept so a caller that only ever sent a key needs no change.
/// </param>
/// <param name="Fields">
///     The credential fields the selected family declares for the selected mode, by name. This is how the modes
///     whose credential is more than one value — an access key id and a secret access key, for instance — are
///     supplied. Which names are accepted comes from the family's own declaration, reported alongside the
///     authentication modes on the permitted-providers endpoint.
/// </param>
public sealed record AiConnectionAuthRequest(
    [property: JsonRequired] string Mode,
    string? ApiKey = null,
    IReadOnlyDictionary<string, string>? Fields = null)
{
    /// <summary>
    ///     Renders the auth settings without the credential. The generated version would print it, and a request
    ///     body is exactly the kind of object that ends up in a log line while a misconfiguration is being
    ///     diagnosed. Field values are elided the same way, and only their names are rendered.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(AiConnectionAuthRequest)} {{ Mode = {this.Mode}, "
               + $"ApiKey = {SecretSafeRendering.Elide(this.ApiKey)}, "
               + $"Fields = [{SecretSafeRendering.KeyNames(this.Fields)}] }}";
    }
}

/// <summary>Configured model payload item for create, update, and discovery flows.</summary>
public sealed record AiConfiguredModelRequest(
    Guid? Id,
    string RemoteModelId,
    string? DisplayName = null,
    IReadOnlyList<AiOperationKind>? OperationKinds = null,
    IReadOnlyList<string>? SupportedProtocolModes = null,
    string? TokenizerName = null,
    int? MaxInputTokens = null,
    int? EmbeddingDimensions = null,
    bool SupportsStructuredOutput = false,
    bool SupportsToolUse = false,
    AiConfiguredModelSource? Source = null,
    DateTimeOffset? LastSeenAt = null,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null,
    int? MaxContextTokens = null,
    decimal? CachedInputCostPer1MUsd = null,
    decimal? CacheWriteCostPer1MUsd = null,
    bool SupportsReasoning = false,
    bool SupportsPromptCaching = false,
    string? ReasoningContentField = null);

/// <summary>Purpose binding payload item for create and update flows.</summary>
public sealed record AiPurposeBindingRequest(
    Guid? Id,
    [property: JsonRequired] AiPurpose Purpose,
    Guid? ConfiguredModelId = null,
    string? RemoteModelId = null,
    string ProtocolMode = ProviderDeclaredProtocolModes.Auto,
    bool IsEnabled = true);

/// <summary>Request body for creating a provider-neutral AI connection profile.</summary>
public sealed record CreateAiConnectionRequest(
    string DisplayName,
    [property: JsonRequired] string ProviderKind,
    string BaseUrl,
    AiConnectionAuthRequest Auth,
    AiDiscoveryMode DiscoveryMode = AiDiscoveryMode.ProviderCatalog,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    IReadOnlyList<AiConfiguredModelRequest>? ConfiguredModels = null,
    IReadOnlyList<AiPurposeBindingRequest>? PurposeBindings = null,
    IReadOnlyDictionary<string, string>? ProviderSettings = null)
{
    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public string EndpointUrl => this.BaseUrl ?? string.Empty;

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Models => (this.ConfiguredModels ?? []).Select(model => model.RemoteModelId).ToList().AsReadOnly();

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public string? ApiKey => this.Auth?.ApiKey;

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public IReadOnlyList<AiConnectionModelCapabilityDto> ModelCapabilities => (this.ConfiguredModels ?? [])
        .Where(model => !string.IsNullOrWhiteSpace(model.TokenizerName) && model.MaxInputTokens.HasValue && model.EmbeddingDimensions.HasValue)
        .Select(model => new AiConnectionModelCapabilityDto(
            model.RemoteModelId,
            model.TokenizerName!,
            model.MaxInputTokens!.Value,
            model.EmbeddingDimensions!.Value,
            model.InputCostPer1MUsd,
            model.OutputCostPer1MUsd,
            model.CachedInputCostPer1MUsd))
        .ToList()
        .AsReadOnly();

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public AiConnectionModelCategory? ModelCategory => null;

    /// <summary>
    ///     Renders the request without the key. The generated version prints every property including the
    ///     <see cref="ApiKey" /> alias, which would defeat the nested auth block's own redaction.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(CreateAiConnectionRequest)} {{ DisplayName = {this.DisplayName}, "
               + $"ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, DiscoveryMode = {this.DiscoveryMode}, "
               + $"Auth = {this.Auth}, ConfiguredModels = {this.ConfiguredModels?.Count ?? 0}, "
               + $"PurposeBindings = {this.PurposeBindings?.Count ?? 0}, "
               + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}], "
               + $"ProviderSettings = [{SecretSafeRendering.KeyNames(this.ProviderSettings)}] }}";
    }
}

/// <summary>Request body for updating an existing provider-neutral AI connection profile.</summary>
public sealed record UpdateAiConnectionRequest(
    string? DisplayName = null,
    string? ProviderKind = null,
    string? BaseUrl = null,
    AiConnectionAuthRequest? Auth = null,
    AiDiscoveryMode? DiscoveryMode = null,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    IReadOnlyList<AiConfiguredModelRequest>? ConfiguredModels = null,
    IReadOnlyList<AiPurposeBindingRequest>? PurposeBindings = null,
    IReadOnlyDictionary<string, string>? ProviderSettings = null)
{
    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public string? EndpointUrl => this.BaseUrl;

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? Models => this.ConfiguredModels?.Select(model => model.RemoteModelId).ToList().AsReadOnly();

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public string? ApiKey => this.Auth?.ApiKey;

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public IReadOnlyList<AiConnectionModelCapabilityDto>? ModelCapabilities => this.ConfiguredModels?
        .Where(model => !string.IsNullOrWhiteSpace(model.TokenizerName) && model.MaxInputTokens.HasValue && model.EmbeddingDimensions.HasValue)
        .Select(model => new AiConnectionModelCapabilityDto(
            model.RemoteModelId,
            model.TokenizerName!,
            model.MaxInputTokens!.Value,
            model.EmbeddingDimensions!.Value,
            model.InputCostPer1MUsd,
            model.OutputCostPer1MUsd,
            model.CachedInputCostPer1MUsd))
        .ToList()
        .AsReadOnly();

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public AiConnectionModelCategory? ModelCategory => null;

    /// <summary>Renders the request without the key; see <see cref="CreateAiConnectionRequest.ToString" />.</summary>
    public override string ToString()
    {
        return $"{nameof(UpdateAiConnectionRequest)} {{ DisplayName = {this.DisplayName}, "
               + $"ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, DiscoveryMode = {this.DiscoveryMode}, "
               + $"Auth = {this.Auth}, ConfiguredModels = {this.ConfiguredModels?.Count ?? 0}, "
               + $"PurposeBindings = {this.PurposeBindings?.Count ?? 0}, "
               + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}], "
               + $"ProviderSettings = [{SecretSafeRendering.KeyNames(this.ProviderSettings)}] }}";
    }
}

/// <summary>Request body for model discovery against a provider without persisting a profile.</summary>
public sealed record DiscoverModelsRequest(
    [property: JsonRequired] string ProviderKind,
    string BaseUrl,
    AiConnectionAuthRequest Auth,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    IReadOnlyDictionary<string, string>? ProviderSettings = null)
{
    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public string EndpointUrl => this.BaseUrl ?? string.Empty;

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public string? ApiKey => this.Auth?.ApiKey;

    /// <summary>Renders the request without the key; see <see cref="CreateAiConnectionRequest.ToString" />.</summary>
    public override string ToString()
    {
        return
            $"{nameof(DiscoverModelsRequest)} {{ ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, "
            + $"Auth = {this.Auth}, DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
            + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}] }}";
    }
}
