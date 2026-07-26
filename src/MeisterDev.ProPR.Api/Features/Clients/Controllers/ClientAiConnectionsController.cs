// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Manages provider-neutral AI connection profiles for a client.</summary>
[ApiController]
[Route("clients/{clientId:guid}/ai-connections")]
public sealed partial class ClientAiConnectionsController(
    IAiConnectionRepository aiConnections,
    IAiProviderDriverRegistry providerDrivers,
    ILogger<ClientAiConnectionsController> logger,
    ITenantProviderPolicyProvider? providerPolicies = null,
    IModelCatalogRepository? modelCatalog = null) : ControllerBase
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

        var policy = providerPolicies is null
            ? TenantProviderPolicy.Unrestricted
            : await providerPolicies.GetForClientAsync(clientId, ct);

        var providers = providerDrivers.RegisteredKinds
            .Select(kind => new PermittedProviderDescriptor(
                kind,
                policy.IsAllowed(kind),
                providerDrivers.GetRequired(kind).SupportedProtocolModes))
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

        // An update can switch the family, so the same refusal applies here as on create.
        if (request.ProviderKind is { } switchedKind && this.RefuseUnimplementedProvider(switchedKind) is { } unimplemented)
        {
            return unimplemented;
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

        var driver = providerDrivers.GetRequired(existing.ProviderKind);

        // Re-validate the stored target before probing: a row saved before this guard existed (or saved in a
        // Development environment) could still carry a target the provider driver now rejects.
        var targetError = driver.ValidateProbeTarget(new AiProbeTarget(existing.BaseUrl, existing.AuthMode, !string.IsNullOrWhiteSpace(existing.Secret)));
        if (targetError is not null)
        {
            this.ModelState.AddModelError("baseUrl", targetError);
            return this.ValidationProblem();
        }

        // Verification reflects connectivity and configured-model reachability only. Whether the product's
        // purposes are satisfied is a client-level concern resolved through logical models and the purpose map,
        // not a per-connection binding requirement.
        var verification = (await driver.VerifyAsync(existing.ToProviderEndpoint(), ct)).ToDto();

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

        var probeOptions = this.TryBuildProbeOptions(request.ProviderKind, request.BaseUrl, request.Auth, request.DefaultHeaders, request.DefaultQueryParams);
        if (probeOptions is null)
        {
            return this.ValidationProblem();
        }

        var driver = providerDrivers.GetRequired(request.ProviderKind);
        var discovered = (await driver.DiscoverModelsAsync(probeOptions.ToProviderEndpoint(), ct)).ToDto(DateTimeOffset.UtcNow);

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
        if (providerPolicies is not null)
        {
            var policy = await providerPolicies.GetForClientAsync(clientId, ct);
            if (policy.DescribeRefusal(request.ProviderKind) is { } refusal)
            {
                this.ModelState.AddModelError("providerKind", $"This profile cannot be probed because {refusal}.");
                return this.ValidationProblem();
            }
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

        var driver = providerDrivers.GetRequired(request.ProviderKind);
        return this.Ok((await driver.VerifyAsync(probeOptions.ToProviderEndpoint(), ct)).ToDto());
    }

    // A provider family this build cannot call is refused where the operator can see it, naming what is
    // available. Without this, opening the enum ahead of a driver would turn into a 500 from the registry.
    private IActionResult? RefuseUnimplementedProvider(AiProviderKind providerKind)
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

    // A binding names the wire shape a call will use, so a shape this provider cannot speak is refused while the
    // operator is looking at the form. The driver refuses it again at call time, but by then a review is running.
    private void RefuseUnspeakableProtocols(AiProviderKind providerKind, IReadOnlyList<AiPurposeBindingDto> bindings)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return;
        }

        var supported = providerDrivers.GetRequired(providerKind).SupportedProtocolModes;
        foreach (var binding in bindings)
        {
            if (AiProtocolModeSupport.DescribeRefusal(providerKind, supported, binding.ProtocolMode) is { } refusal)
            {
                this.ModelState.AddModelError(
                    "purposeBindings",
                    $"The '{binding.Purpose}' binding cannot be saved because {refusal}.");
            }
        }
    }

    private static AiConnectionProbeOptionsDto ToProbeOptions(AiConnectionDto connection)
    {
        return new AiConnectionProbeOptionsDto(
            connection.ProviderKind,
            connection.BaseUrl,
            connection.AuthMode,
            connection.Secret,
            connection.DefaultHeaders,
            connection.DefaultQueryParams);
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

        var configuredModels = this.NormalizeConfiguredModels(request.ConfiguredModels);
        var purposeBindings = this.NormalizePurposeBindings(request.PurposeBindings, configuredModels);
        this.RefuseUnspeakableProtocols(request.ProviderKind, purposeBindings);

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
            probeOptions.Secret);
    }

    private AiConnectionWriteRequestDto? TryBuildWriteRequest(AiConnectionDto existing, UpdateAiConnectionRequest request)
    {
        var providerKind = request.ProviderKind ?? existing.ProviderKind;
        var effectiveApiKey = request.Auth is null || string.IsNullOrWhiteSpace(request.Auth.ApiKey)
            ? existing.Secret
            : request.Auth.ApiKey;
        var auth = request.Auth is null
            ? new AiConnectionAuthRequest(existing.AuthMode, existing.Secret)
            : new AiConnectionAuthRequest(request.Auth.Mode, effectiveApiKey);
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

        var configuredModels = this.NormalizeConfiguredModels(request.ConfiguredModels ?? existing.ConfiguredModels.Select(ToConfiguredModelRequest).ToList());
        var purposeBindings = this.NormalizePurposeBindings(
            request.PurposeBindings ?? existing.PurposeBindings.Select(ToBindingRequest).ToList(),
            configuredModels);
        this.RefuseUnspeakableProtocols(providerKind, purposeBindings);

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
            probeOptions.Secret);
    }

    private AiConnectionProbeOptionsDto? TryBuildProbeOptions(
        AiProviderKind providerKind,
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

        // Provider-specific base-URL / SSRF-egress / auth-shape validation lives behind the driver seam,
        // so the controller does not branch on provider kind.
        var targetError = providerDrivers.GetRequired(providerKind)
            .ValidateProbeTarget(new AiProbeTarget(baseUrl.Trim(), auth.Mode, !string.IsNullOrWhiteSpace(auth.ApiKey)));
        if (targetError is not null)
        {
            this.ModelState.AddModelError(nameof(baseUrl), targetError);
            return null;
        }

        var secret = auth.Mode == AiAuthMode.ApiKey ? auth.ApiKey?.Trim() : null;
        return new AiConnectionProbeOptionsDto(
            providerKind,
            baseUrl.Trim(),
            auth.Mode,
            secret,
            NormalizeMap(defaultHeaders),
            NormalizeMap(defaultQueryParams));
    }

    private IReadOnlyList<AiConfiguredModelDto> NormalizeConfiguredModels(IReadOnlyList<AiConfiguredModelRequest>? requestModels)
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
            var normalizedModel = this.NormalizeConfiguredModel(requestModel, seen);
            if (normalizedModel is not null)
            {
                models.Add(normalizedModel);
            }
        }

        return models.AsReadOnly();
    }

    private AiConfiguredModelDto? NormalizeConfiguredModel(AiConfiguredModelRequest requestModel, HashSet<string> seen)
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
        var defaultProtocolModes = isEmbeddingOnly
            ? new List<AiProtocolMode> { AiProtocolMode.Auto, AiProtocolMode.Embeddings }.AsReadOnly()
            : new List<AiProtocolMode> { AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions }.AsReadOnly();
        var protocolModes = requestModel.SupportedProtocolModes is { Count: > 0 }
            ? requestModel.SupportedProtocolModes.Distinct().ToList().AsReadOnly()
            : defaultProtocolModes;

        if (operationKinds.Contains(AiOperationKind.Embedding))
        {
            this.AddEmbeddingModelErrors(requestModel, remoteModelId);
        }

        if (protocolModes.Contains(AiProtocolMode.Embeddings) && !operationKinds.Contains(AiOperationKind.Embedding))
        {
            this.ModelState.AddModelError(
                RequestModelsPropertyName,
                $"Model '{remoteModelId}' cannot declare the embeddings protocol without embedding capability.");
        }

        if ((protocolModes.Contains(AiProtocolMode.ChatCompletions) || protocolModes.Contains(AiProtocolMode.Responses)) &&
            !operationKinds.Contains(AiOperationKind.Chat))
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
            requestModel.CachedInputCostPer1MUsd);
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
        if (requestBindings is null || requestBindings.Count == 0)
        {
            this.ModelState.AddModelError(nameof(requestBindings), "purposeBindings must contain at least one binding.");
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

            if (requestBinding.ProtocolMode is not AiProtocolMode.Auto and not AiProtocolMode.Embeddings)
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

        if (requestBinding.ProtocolMode != AiProtocolMode.Auto && !model.SupportedProtocolModes.Contains(requestBinding.ProtocolMode))
        {
            this.ModelState.AddModelError(
                RequestBindingsPropertyName,
                $"Model '{model.RemoteModelId}' does not support protocol '{requestBinding.ProtocolMode}'.");
        }
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
            model.CachedInputCostPer1MUsd);
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
public sealed record ProbeAiConnectionRequest(
    [property: JsonRequired] AiProviderKind ProviderKind,
    string BaseUrl,
    AiConnectionAuthRequest Auth,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null)
{
    /// <summary>Renders the request without the key; see <see cref="CreateAiConnectionRequest.ToString" />.</summary>
    public override string ToString()
    {
        return $"{nameof(ProbeAiConnectionRequest)} {{ ProviderKind = {this.ProviderKind}, BaseUrl = {this.BaseUrl}, "
               + $"Auth = {this.Auth}, DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}] }}";
    }
}

/// <summary>One provider family this build can call, and what a given client may do with it.</summary>
/// <param name="ProviderKind">The provider family.</param>
/// <param name="IsPermitted">Whether the client's tenant permits it.</param>
/// <param name="ProtocolModes">
///     The wire shapes this provider's driver can speak. Sent so the configuration UI offers only shapes that can
///     actually be called, rather than keeping a second copy of the drivers' knowledge.
/// </param>
public sealed record PermittedProviderDescriptor(
    AiProviderKind ProviderKind,
    bool IsPermitted,
    IReadOnlyList<AiProtocolMode> ProtocolModes);

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
public sealed record AiConnectionAuthRequest([property: JsonRequired] AiAuthMode Mode, string? ApiKey = null)
{
    /// <summary>
    ///     Renders the auth settings without the key. The generated version would print it, and a request body is
    ///     exactly the kind of object that ends up in a log line while a misconfiguration is being diagnosed.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(AiConnectionAuthRequest)} {{ Mode = {this.Mode}, ApiKey = {SecretSafeRendering.Elide(this.ApiKey)} }}";
    }
}

/// <summary>Configured model payload item for create, update, and discovery flows.</summary>
public sealed record AiConfiguredModelRequest(
    Guid? Id,
    string RemoteModelId,
    string? DisplayName = null,
    IReadOnlyList<AiOperationKind>? OperationKinds = null,
    IReadOnlyList<AiProtocolMode>? SupportedProtocolModes = null,
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
    decimal? CachedInputCostPer1MUsd = null);

/// <summary>Purpose binding payload item for create and update flows.</summary>
public sealed record AiPurposeBindingRequest(
    Guid? Id,
    [property: JsonRequired] AiPurpose Purpose,
    Guid? ConfiguredModelId = null,
    string? RemoteModelId = null,
    AiProtocolMode ProtocolMode = AiProtocolMode.Auto,
    bool IsEnabled = true);

/// <summary>Request body for creating a provider-neutral AI connection profile.</summary>
public sealed record CreateAiConnectionRequest(
    string DisplayName,
    [property: JsonRequired] AiProviderKind ProviderKind,
    string BaseUrl,
    AiConnectionAuthRequest Auth,
    AiDiscoveryMode DiscoveryMode = AiDiscoveryMode.ProviderCatalog,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    IReadOnlyList<AiConfiguredModelRequest>? ConfiguredModels = null,
    IReadOnlyList<AiPurposeBindingRequest>? PurposeBindings = null)
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
               + $"ProviderKind = {this.ProviderKind}, BaseUrl = {this.BaseUrl}, DiscoveryMode = {this.DiscoveryMode}, "
               + $"Auth = {this.Auth}, ConfiguredModels = {this.ConfiguredModels?.Count ?? 0}, "
               + $"PurposeBindings = {this.PurposeBindings?.Count ?? 0}, "
               + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}] }}";
    }
}

/// <summary>Request body for updating an existing provider-neutral AI connection profile.</summary>
public sealed record UpdateAiConnectionRequest(
    string? DisplayName = null,
    AiProviderKind? ProviderKind = null,
    string? BaseUrl = null,
    AiConnectionAuthRequest? Auth = null,
    AiDiscoveryMode? DiscoveryMode = null,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    IReadOnlyList<AiConfiguredModelRequest>? ConfiguredModels = null,
    IReadOnlyList<AiPurposeBindingRequest>? PurposeBindings = null)
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
               + $"ProviderKind = {this.ProviderKind}, BaseUrl = {this.BaseUrl}, DiscoveryMode = {this.DiscoveryMode}, "
               + $"Auth = {this.Auth}, ConfiguredModels = {this.ConfiguredModels?.Count ?? 0}, "
               + $"PurposeBindings = {this.PurposeBindings?.Count ?? 0}, "
               + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}] }}";
    }
}

/// <summary>Request body for model discovery against a provider without persisting a profile.</summary>
public sealed record DiscoverModelsRequest(
    [property: JsonRequired] AiProviderKind ProviderKind,
    string BaseUrl,
    AiConnectionAuthRequest Auth,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null)
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
        return $"{nameof(DiscoverModelsRequest)} {{ ProviderKind = {this.ProviderKind}, BaseUrl = {this.BaseUrl}, "
               + $"Auth = {this.Auth}, DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}] }}";
    }
}
