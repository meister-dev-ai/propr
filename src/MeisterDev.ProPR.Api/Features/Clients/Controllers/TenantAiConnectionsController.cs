// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Tenant-scoped AI connections: connection profiles defined at the tenant and inherited (read-only) by the
///     tenant's clients, referenced by tenant-catalog logical models. Requires the tenant-administrator role.
///     Tenant connections do not participate in a client's active/tier selection — they are only referenced by
///     logical models, which resolve them by global id at runtime.
/// </summary>
[ApiController]
[Route("tenants/{tenantId:guid}/ai-connections")]
public sealed class TenantAiConnectionsController(
    IAiConnectionRepository connections,
    IAiProviderDriverRegistry providerDrivers,
    ITenantProviderPolicyProvider providerPolicies,
    EgressUrlPolicy egressPolicy,
    IProviderConnectionContextFactory? hostContexts = null,
    IProviderAddInCapabilityGate? addInCapabilities = null,
    IProviderHttpClientFactory? providerHttp = null) : ControllerBase
{
    // What a family may do while answering for a connection whose bound primitives cannot be built: the
    // transport and nothing else. Verification is a call the family makes to the endpoint, and a family reaches
    // the network only through the factory the host supplies.
    private IProviderConnectionContext? ProbeContext()
    {
        return providerHttp is null ? null : new ProviderProbeContext(providerHttp);
    }

    /// <summary>Lists the tenant's connection profiles (with their configured models).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AiConnectionDto>), 200)]
    public async Task<IActionResult> List(Guid tenantId, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        return this.Ok(await connections.GetByTenantAsync(tenantId, ct));
    }

    /// <summary>
    ///     Lists the provider families this build has a driver for, each flagged with whether this tenant's own
    ///     policy permits it, and described well enough to configure one: its name, the shapes it speaks and
    ///     authenticates with, the fields each authentication mode needs, and what it declares about the connection
    ///     form.
    /// </summary>
    /// <remarks>
    ///     The same answer the client-scoped endpoint gives, read against the tenant's policy directly. It is
    ///     served here so the tenant screens — the connection editor and the provider allow-list — describe
    ///     families from the installation rather than from a table of their own, which would name only the
    ///     families that existed when the console shipped.
    /// </remarks>
    [HttpGet("permitted-providers")]
    [ProducesResponseType(typeof(PermittedProvidersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPermittedProviders(Guid tenantId, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        var policy = await providerPolicies.GetForTenantAsync(tenantId, ct);

        var providers = providerDrivers.RegisteredKinds
            .Select(providerDrivers.GetRequired)
            .Select(driver => PermittedProviderDescriptor.Describe(driver, policy.IsAllowed(driver.Declaration.Key)))
            .ToList();

        return this.Ok(new PermittedProvidersResponse(providers, policy.IsRestricted));
    }

    /// <summary>Creates a tenant-scoped connection profile.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(AiConnectionDto), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Create(Guid tenantId, [FromBody] CreateAiConnectionRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
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
            var created = await connections.AddTenantAsync(tenantId, writeRequest, ct);
            return this.CreatedAtAction(nameof(this.List), new { tenantId }, created);
        }
        catch (ProviderDeclaredValueRefusedException ex)
        {
            this.ModelState.AddModelError($"providerSettings.{ex.FieldName}", ex.Reason);
            return this.ValidationProblem();
        }
    }

    /// <summary>Deletes a tenant connection profile. 404 if it is not this tenant's.</summary>
    [HttpDelete("{connectionId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete(Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        var existing = await connections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.TenantId != tenantId)
        {
            return this.NotFound();
        }

        await connections.DeleteAsync(connectionId, ct);
        return this.NoContent();
    }

    /// <summary>Verifies a tenant connection against its provider. 404 if it is not this tenant's.</summary>
    [HttpPost("{connectionId:guid}/verify")]
    [ProducesResponseType(typeof(AiVerificationResultDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Verify(Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        var existing = await connections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.TenantId != tenantId)
        {
            return this.NotFound();
        }

        // The tenant's policy has two legs and verification sends the stored credential, so both are read here
        // rather than only where the profile was saved. A tenant that forbade the family, or added or tightened
        // its host list, after this profile was stored would otherwise still have the credential dialled out.
        var policy = await providerPolicies.GetForTenantAsync(tenantId, ct);
        if (policy.GetRefusalReason(existing.ProviderKind) is { } refusal)
        {
            this.ModelState.AddModelError("providerKind", $"This profile cannot be verified because {refusal}.");
            return this.ValidationProblem();
        }

        var reachRefusal = policy.DescribeReachRefusal(
            existing.BaseUrl,
            providerDrivers.ReachedHostPatterns(existing.ProviderKind));
        if (reachRefusal is not null)
        {
            this.ModelState.AddModelError("baseUrl", $"This profile cannot be verified because {reachRefusal}.");
            return this.ValidationProblem();
        }

        if (this.RefuseUnimplementedProvider(existing.ProviderKind) is { } unimplemented)
        {
            return unimplemented;
        }

        // Verification sends the stored credential to the family, which is a use of the family, so it needs the
        // same entitlement the save needed. An installation whose licence lapsed after the profile was saved
        // would otherwise keep verifying it.
        if (await this.RefuseUnlicensedProviderAsync(existing.ProviderKind, ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var driver = providerDrivers.GetRequired(existing.ProviderKind);

        // The addresses the family declared are re-checked against the installation's egress rules, which can
        // have tightened since the profile was saved.
        var declaredEgressRefusals = DeclaredUrlFloor.FindRefusedAddressFields(
            driver.Declaration,
            existing.ProviderSettings ?? new Dictionary<string, string>(StringComparer.Ordinal),
            egressPolicy);
        if (declaredEgressRefusals.Count > 0)
        {
            foreach (var (field, message) in declaredEgressRefusals)
            {
                this.ModelState.AddModelError($"providerSettings.{field}", message);
            }

            return this.ValidationProblem();
        }

        this.RefuseUndeclaredAuthMode(existing.ProviderKind, existing.AuthMode, refusedAction: "verified");
        if (!this.ModelState.IsValid)
        {
            return this.ValidationProblem();
        }

        // The stored target is checked again before it is contacted. A row saved before this check existed, or
        // saved while private egress was permitted, can hold a URL the driver now refuses, and verifying it
        // would send the stored credential there.
        var targetError = driver.ValidateProbeTarget(this.ProbeTarget(existing.BaseUrl, existing.AuthMode, !string.IsNullOrWhiteSpace(existing.Secret)));
        if (targetError is not null)
        {
            this.ModelState.AddModelError("baseUrl", targetError);
            return this.ValidationProblem();
        }

        // The stored credential is checked against the declaration for the same reason: a profile holding
        // fields the family does not read, or missing ones it does, fails at the provider with a message that
        // names neither. A credential of one field comes back under the generic name, so it is read under the
        // name the stored mode declares before it is checked.
        this.RefuseUnusableCredential(
            existing.ProviderKind,
            existing.AuthMode,
            AiCredentialFieldSupport.Adopt(
                driver.Declaration.CredentialFieldsFor(existing.AuthMode),
                ProviderCredentialValues.From(ProviderSecretEnvelope.Decode(existing.Secret, existing.AuthMode).Fields)));
        if (!this.ModelState.IsValid)
        {
            return this.ValidationProblem();
        }

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
        await connections.SaveVerificationAsync(connectionId, verification, ct);
        return this.Ok(verification);
    }

    private IActionResult? RequireTenantAdmin(Guid tenantId)
    {
        return AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
    }

    // What a family is asked to validate, carrying what this installation permits an address to reach. A family
    // compiled into the host reads those settings where it is composed; one loaded from a directory is
    // constructed with no arguments, so the target is where it learns them. The installation's own check has
    // already run, so this widens nothing: it only keeps a family from refusing what the host admitted.
    private AiProbeTarget ProbeTarget(string baseUrl, string authMode, bool hasApiKey)
    {
        return new AiProbeTarget(baseUrl, authMode, hasApiKey)
        {
            AllowsPrivateAddress = egressPolicy.AllowPrivateEgress,
            AllowsInsecureScheme = egressPolicy.AllowInsecureScheme,
        };
    }

    // An authentication mode the family does not declare cannot be used: the credential would be written where
    // that provider does not read it, and the failure would come back as a provider-worded rejection naming
    // neither the mode nor the family. Saving is refused for that reason, and so is verifying a profile that was
    // stored before the rule existed.
    // A stored family this build has no driver for is answered as a refusal naming it, the way the client-scoped
    // controller answers the same case. Resolving it instead would raise the registry's own error, which names
    // the family but not the profile and reaches the operator as a server fault.
    // The licence check on the family a connection is being written against, beside the driver check for the
    // same reason: both answer whether this installation can use the family at all.
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

    private void RefuseUndeclaredAuthMode(string providerKind, string authMode, string refusedAction)
    {
        var supported = providerDrivers.GetRequired(providerKind).SupportedAuthModes;
        if (AiAuthModeSupport.GetRefusalReason(providerKind, supported, authMode) is { } refusal)
        {
            this.ModelState.AddModelError("authMode", $"This profile cannot be {refusedAction} because {refusal}.");
        }
    }

    // A required credential field left empty produces a profile that fails on its first call, and a field the
    // family never declared is a value the operator believes is in use and nothing reads. Both are refused where
    // the operator can see them, naming the field. Skipped for a mode the family does not declare, because the
    // mode is then the problem and is refused as such.
    private void RefuseUnusableCredential(
        string providerKind,
        string authMode,
        ProviderCredentialValues fields)
    {
        var driver = providerDrivers.GetRequired(providerKind);
        if (!driver.Declaration.AuthModes.Any(mode => ProviderVocabulary.ValuesEqual(mode.Mode, authMode)))
        {
            return;
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
    }

    // A tenant connection is a provider + credentials + configured models — no purpose bindings (those are a
    // client-level concern resolved through logical models). Provider-specific base-URL / SSRF / auth-shape
    // validation lives behind the driver seam, mirroring the client connection create.
    private AiConnectionWriteRequestDto? TryBuildWriteRequest(CreateAiConnectionRequest request)
    {
        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200)
        {
            this.ModelState.AddModelError(nameof(request.DisplayName), "displayName is required and must be 200 characters or fewer.");
            return null;
        }

        var baseUrl = request.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 1000 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            this.ModelState.AddModelError(nameof(request.BaseUrl), "baseUrl is required, must be an absolute URL, and must be 1000 characters or fewer.");
            return null;
        }

        if (request.Auth is null)
        {
            this.ModelState.AddModelError(nameof(request.Auth), "auth is required.");
            return null;
        }

        // The installation's own egress rules come first, so a family cannot admit an address this installation
        // refuses. A family may still refuse more, which the driver check below is for.
        if (egressPolicy.GetRefusalReason(baseUrl, "baseUrl") is { } egressRefusal)
        {
            this.ModelState.AddModelError(nameof(request.BaseUrl), egressRefusal);
            return null;
        }

        var driver = providerDrivers.GetRequired(request.ProviderKind);
        var fields = AiConnectionCredential.Collect(request.Auth);

        // A authentication mode is read against the family before anything else uses it, so a caller naming the
        // spelling this family supersedes is understood and the value is stored under the name the family
        // declares now. A shape nothing claims is carried as it was sent, and refused below by name.
        var authMode = providerDrivers
            .ResolveAuthMode(request.ProviderKind, request.Auth.Mode)
            .ValueOr(request.Auth.Mode?.Trim() ?? string.Empty);

        var targetError = driver.ValidateProbeTarget(this.ProbeTarget(baseUrl, authMode, fields.Count > 0));
        if (targetError is not null)
        {
            this.ModelState.AddModelError(nameof(request.BaseUrl), targetError);
            return null;
        }

        this.RefuseUndeclaredAuthMode(request.ProviderKind, authMode, refusedAction: "saved");
        this.RefuseUnusableCredential(request.ProviderKind, authMode, ProviderCredentialValues.From(fields));

        // The size bound on what is written into the protected column, which is the same bound a provider
        // family's own credential is held to.
        foreach (var refusal in AiConnectionCredential.FindCredentialSizeRefusals(fields))
        {
            this.ModelState.AddModelError("auth", refusal);
        }

        var declared = AiConnectionDeclaredValues.Collect(driver.Declaration, request.ProviderSettings);
        foreach (var (field, message) in declared.Refusals
                     .Concat(DeclaredValueValidation.Refusals(driver, declared, egressPolicy)))
        {
            this.ModelState.AddModelError($"providerSettings.{field}", message);
        }

        var models = this.MapModels(request.ProviderKind, request.ConfiguredModels);
        if (!this.ModelState.IsValid)
        {
            return null;
        }

        var secret = AiConnectionCredential.Encode(authMode, fields);
        return new AiConnectionWriteRequestDto(
            displayName,
            request.ProviderKind,
            baseUrl,
            authMode,
            request.DiscoveryMode,
            models,
            [],
            request.DefaultHeaders,
            request.DefaultQueryParams,
            secret,
            declared.Settings,
            declared.Secrets);
    }

    private IReadOnlyList<AiConfiguredModelDto> MapModels(
        string providerKind,
        IReadOnlyList<AiConfiguredModelRequest>? requestModels)
    {
        if (requestModels is null || requestModels.Count == 0)
        {
            this.ModelState.AddModelError("configuredModels", "configuredModels must contain at least one model.");
            return [];
        }

        var models = new List<AiConfiguredModelDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in requestModels)
        {
            var remoteModelId = model.RemoteModelId?.Trim();
            if (string.IsNullOrWhiteSpace(remoteModelId))
            {
                this.ModelState.AddModelError("configuredModels", "Each configured model requires remoteModelId.");
                continue;
            }

            if (!seen.Add(remoteModelId))
            {
                this.ModelState.AddModelError("configuredModels", $"Configured model '{remoteModelId}' is duplicated.");
                continue;
            }

            var operationKinds = model.OperationKinds is { Count: > 0 }
                ? model.OperationKinds.Distinct().ToList().AsReadOnly()
                : new List<AiOperationKind> { AiOperationKind.Chat }.AsReadOnly();
            var isEmbedding = operationKinds.Contains(AiOperationKind.Embedding);

            if (isEmbedding && (string.IsNullOrWhiteSpace(model.TokenizerName) || !model.MaxInputTokens.HasValue || !model.EmbeddingDimensions.HasValue))
            {
                this.ModelState.AddModelError("configuredModels", $"Embedding model '{remoteModelId}' requires tokenizer, max input tokens, and dimensions.");
                continue;
            }

            var protocolModes = this.ResolveProtocolModes(providerKind, model, isEmbedding);

            models.Add(
                new AiConfiguredModelDto(
                    model.Id ?? Guid.Empty,
                    remoteModelId,
                    string.IsNullOrWhiteSpace(model.DisplayName) ? remoteModelId : model.DisplayName.Trim(),
                    operationKinds,
                    protocolModes,
                    string.IsNullOrWhiteSpace(model.TokenizerName) ? null : model.TokenizerName.Trim(),
                    model.MaxInputTokens,
                    model.EmbeddingDimensions,
                    model.SupportsStructuredOutput,
                    model.SupportsToolUse,
                    model.Source ?? AiConfiguredModelSource.Manual,
                    model.LastSeenAt,
                    model.InputCostPer1MUsd,
                    model.OutputCostPer1MUsd,
                    model.MaxContextTokens,
                    model.CachedInputCostPer1MUsd,
                    model.CacheWriteCostPer1MUsd,
                    model.SupportsReasoning,
                    model.SupportsPromptCaching,
                    string.IsNullOrWhiteSpace(model.ReasoningContentField) ? null : model.ReasoningContentField.Trim()));
        }

        return models.AsReadOnly();
    }

    // The shapes a model serves when the request states none. An embedding model gets the two the host
    // reserves; anything else gets what the family declares it speaks, without the embeddings shape. A family
    // this build has no driver for declares nothing, so the automatic shape stands alone, which it
    // means: the driver picks the wire format.
    private IReadOnlyList<string> ResolveProtocolModes(
        string providerKind,
        AiConfiguredModelRequest model,
        bool isEmbedding)
    {
        if (model.SupportedProtocolModes is { Count: > 0 })
        {
            return model.SupportedProtocolModes.Distinct(ProviderVocabulary.ValueComparer).ToList().AsReadOnly();
        }

        if (isEmbedding)
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
}
