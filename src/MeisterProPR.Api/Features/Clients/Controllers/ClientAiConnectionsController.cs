// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.Providers;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages provider-neutral AI connection profiles for a client.</summary>
[ApiController]
public sealed partial class ClientAiConnectionsController(
    IAiConnectionRepository aiConnections,
    IAiProviderDriverRegistry providerDrivers,
    ILogger<ClientAiConnectionsController> logger) : ControllerBase
{
    private static readonly StringComparer ModelNameComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly AiPurpose[] RequiredPurposes =
    [
        AiPurpose.ReviewDefault,
        AiPurpose.MemoryReconsideration,
        AiPurpose.EmbeddingDefault,
    ];

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
    [HttpGet("clients/{clientId:guid}/ai-connections")]
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

    /// <summary>Creates a new AI connection profile for the specified client.</summary>
    [HttpPost("clients/{clientId:guid}/ai-connections")]
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

        var writeRequest = this.TryBuildWriteRequest(request);
        if (writeRequest is null)
        {
            return this.ValidationProblem();
        }

        var connection = await aiConnections.AddAsync(clientId, writeRequest, ct);
        LogConnectionCreated(logger, connection.Id, clientId);
        return this.CreatedAtAction(nameof(this.GetAiConnections), new { clientId }, connection);
    }

    /// <summary>Updates an existing AI connection profile for the specified client.</summary>
    [HttpPatch("clients/{clientId:guid}/ai-connections/{connectionId:guid}")]
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

        var writeRequest = this.TryBuildWriteRequest(existing, request);
        if (writeRequest is null)
        {
            return this.ValidationProblem();
        }

        if (!await aiConnections.UpdateAsync(connectionId, writeRequest, ct))
        {
            return this.NotFound();
        }

        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        LogConnectionUpdated(logger, connectionId, clientId);
        return this.Ok(refreshed);
    }

    /// <summary>Deletes an AI connection profile.</summary>
    [HttpDelete("clients/{clientId:guid}/ai-connections/{connectionId:guid}")]
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
    [HttpPost("clients/{clientId:guid}/ai-connections/{connectionId:guid}/activate")]
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

        if (!await aiConnections.ActivateAsync(connectionId, ct))
        {
            return this.BadRequest(
                new
                {
                    error =
                        "Activation requires a freshly verified profile with valid Review Default, Memory Reconsideration, and Embedding Default bindings. Re-verify the profile after connectivity, auth, model, or binding edits.",
                });
        }

        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        LogConnectionActivated(logger, connectionId, clientId);
        return this.Ok(refreshed);
    }

    /// <summary>Deactivates an AI connection profile.</summary>
    [HttpPost("clients/{clientId:guid}/ai-connections/{connectionId:guid}/deactivate")]
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
    [HttpPost("clients/{clientId:guid}/ai-connections/{connectionId:guid}/verify")]
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
        var verification = await driver.VerifyAsync(ToProbeOptions(existing), ct);

        if (verification.Status == AiVerificationStatus.Verified)
        {
            var bindingFailure = this.ValidateRequiredBindings(existing);
            if (bindingFailure is not null)
            {
                verification = new AiVerificationResultDto(
                    AiVerificationStatus.Failed,
                    AiVerificationFailureCategory.CapabilityMismatch,
                    bindingFailure,
                    "Complete the Review Default, Memory Reconsideration, and Embedding Default bindings and ensure each selected model supports the bound workload.",
                    DateTimeOffset.UtcNow,
                    verification.Warnings,
                    verification.DriverMetadata);
            }
        }

        await aiConnections.SaveVerificationAsync(connectionId, verification, ct);
        LogConnectionVerified(logger, connectionId, clientId, verification.Status);
        return this.Ok(verification);
    }

    /// <summary>Discovers provider models using the supplied unsaved profile settings.</summary>
    [HttpPost("clients/{clientId:guid}/ai-connections/discover-models")]
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

        var probeOptions = this.TryBuildProbeOptions(request.ProviderKind, request.BaseUrl, request.Auth, request.DefaultHeaders, request.DefaultQueryParams);
        if (probeOptions is null)
        {
            return this.ValidationProblem();
        }

        var driver = providerDrivers.GetRequired(request.ProviderKind);
        return this.Ok(await driver.DiscoverModelsAsync(probeOptions, ct));
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

    private string? ValidateRequiredBindings(AiConnectionDto connection)
    {
        foreach (var purpose in RequiredPurposes)
        {
            var binding = FindBinding(connection, purpose);
            if (binding is null)
            {
                return $"Required binding '{purpose}' is missing or disabled.";
            }

            var model = connection.ConfiguredModels.FirstOrDefault(candidate => candidate.Id == binding.ConfiguredModelId)
                        ?? connection.ConfiguredModels.FirstOrDefault(candidate =>
                            string.Equals(candidate.RemoteModelId, binding.RemoteModelId, StringComparison.OrdinalIgnoreCase));
            if (model is null)
            {
                return $"Required binding '{purpose}' references an unknown configured model.";
            }

            if (purpose == AiPurpose.EmbeddingDefault)
            {
                if (!model.SupportsEmbedding || string.IsNullOrWhiteSpace(model.TokenizerName) || !model.EmbeddingDimensions.HasValue)
                {
                    return $"Binding '{purpose}' must target a model with embedding capability metadata.";
                }

                if (binding.ProtocolMode is not AiProtocolMode.Auto and not AiProtocolMode.Embeddings)
                {
                    return $"Binding '{purpose}' must use the embeddings protocol or automatic mode.";
                }

                continue;
            }

            if (!model.SupportsChat)
            {
                return $"Binding '{purpose}' must target a chat-capable model.";
            }

            if (binding.ProtocolMode != AiProtocolMode.Auto && !model.SupportedProtocolModes.Contains(binding.ProtocolMode))
            {
                return $"Binding '{purpose}' uses protocol '{binding.ProtocolMode}' which is not supported by model '{model.RemoteModelId}'.";
            }
        }

        return null;
    }

    private static AiPurposeBindingDto? FindBinding(AiConnectionDto connection, AiPurpose purpose)
    {
        var binding = connection.PurposeBindings.FirstOrDefault(candidate => candidate.Purpose == purpose && candidate.IsEnabled);

        if (binding is not null || !IsReviewEffortOverride(purpose))
        {
            return binding;
        }

        return connection.PurposeBindings.FirstOrDefault(candidate =>
            candidate.Purpose == AiPurpose.ReviewDefault && candidate.IsEnabled);
    }

    private static bool IsReviewEffortOverride(AiPurpose purpose)
    {
        return purpose is AiPurpose.ReviewLowEffort or AiPurpose.ReviewMediumEffort or AiPurpose.ReviewHighEffort;
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
        var auth = request.Auth is null
            ? new AiConnectionAuthRequest(existing.AuthMode, existing.Secret)
            : new AiConnectionAuthRequest(
                request.Auth.Mode,
                string.IsNullOrWhiteSpace(request.Auth.ApiKey) ? existing.Secret : request.Auth.ApiKey);
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

        if (providerKind == AiProviderKind.OpenAi && IsAzureHostedOpenAiEndpoint(baseUrl))
        {
            this.ModelState.AddModelError(
                nameof(baseUrl),
                "Azure-hosted OpenAI endpoints, including Azure AI Foundry OpenAI endpoints, must use providerKind 'azureOpenAi' instead of 'openAi'.");
            return null;
        }

        if (auth is null)
        {
            this.ModelState.AddModelError(nameof(auth), "auth is required.");
            return null;
        }

        if (providerKind == AiProviderKind.AzureOpenAi && auth.Mode == AiAuthMode.AzureIdentity)
        {
            return new AiConnectionProbeOptionsDto(
                providerKind,
                baseUrl.Trim(),
                auth.Mode,
                null,
                NormalizeMap(defaultHeaders),
                NormalizeMap(defaultQueryParams));
        }

        if (auth.Mode != AiAuthMode.ApiKey || string.IsNullOrWhiteSpace(auth.ApiKey))
        {
            this.ModelState.AddModelError(nameof(auth.ApiKey), "An API key is required for this provider and auth mode.");
            return null;
        }

        return new AiConnectionProbeOptionsDto(
            providerKind,
            baseUrl.Trim(),
            auth.Mode,
            auth.ApiKey.Trim(),
            NormalizeMap(defaultHeaders),
            NormalizeMap(defaultQueryParams));
    }

    private static bool IsAzureHostedOpenAiEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);
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
            if (string.IsNullOrWhiteSpace(requestModel.RemoteModelId))
            {
                this.ModelState.AddModelError(nameof(requestModels), "Each configured model requires remoteModelId.");
                continue;
            }

            var remoteModelId = requestModel.RemoteModelId.Trim();
            if (!seen.Add(remoteModelId))
            {
                this.ModelState.AddModelError(nameof(requestModels), $"Configured model '{remoteModelId}' is duplicated.");
                continue;
            }

            var inferredEmbedding = IsEmbeddingModel(remoteModelId, requestModel);
            var operationKinds = requestModel.OperationKinds is { Count: > 0 }
                ? requestModel.OperationKinds.Distinct().ToList().AsReadOnly()
                : inferredEmbedding
                    ? new List<AiOperationKind> { AiOperationKind.Embedding }.AsReadOnly()
                    : new List<AiOperationKind> { AiOperationKind.Chat }.AsReadOnly();

            var protocolModes = requestModel.SupportedProtocolModes is { Count: > 0 }
                ? requestModel.SupportedProtocolModes.Distinct().ToList().AsReadOnly()
                : operationKinds.Contains(AiOperationKind.Embedding) && !operationKinds.Contains(AiOperationKind.Chat)
                    ? new List<AiProtocolMode> { AiProtocolMode.Auto, AiProtocolMode.Embeddings }.AsReadOnly()
                    : new List<AiProtocolMode> { AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions }.AsReadOnly();

            if (operationKinds.Contains(AiOperationKind.Embedding))
            {
                if (string.IsNullOrWhiteSpace(requestModel.TokenizerName))
                {
                    this.ModelState.AddModelError(nameof(requestModels), $"Embedding model '{remoteModelId}' requires tokenizerName.");
                }

                if (!requestModel.MaxInputTokens.HasValue || requestModel.MaxInputTokens.Value <= 0)
                {
                    this.ModelState.AddModelError(nameof(requestModels), $"Embedding model '{remoteModelId}' requires maxInputTokens greater than zero.");
                }

                if (!requestModel.EmbeddingDimensions.HasValue || requestModel.EmbeddingDimensions.Value is < 64 or > 4096)
                {
                    this.ModelState.AddModelError(
                        nameof(requestModels),
                        $"Embedding model '{remoteModelId}' requires embeddingDimensions between 64 and 4096.");
                }
            }

            if (protocolModes.Contains(AiProtocolMode.Embeddings) && !operationKinds.Contains(AiOperationKind.Embedding))
            {
                this.ModelState.AddModelError(
                    nameof(requestModels),
                    $"Model '{remoteModelId}' cannot declare the embeddings protocol without embedding capability.");
            }

            if ((protocolModes.Contains(AiProtocolMode.ChatCompletions) || protocolModes.Contains(AiProtocolMode.Responses)) &&
                !operationKinds.Contains(AiOperationKind.Chat))
            {
                this.ModelState.AddModelError(nameof(requestModels), $"Model '{remoteModelId}' cannot declare chat protocols without chat capability.");
            }

            models.Add(
                new AiConfiguredModelDto(
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
                    requestModel.OutputCostPer1MUsd));
        }

        return models.AsReadOnly();
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
            if (!seenPurposes.Add(requestBinding.Purpose))
            {
                this.ModelState.AddModelError(nameof(requestBindings), $"Purpose '{requestBinding.Purpose}' is duplicated.");
                continue;
            }

            AiConfiguredModelDto? model = null;
            if (requestBinding.ConfiguredModelId.HasValue && requestBinding.ConfiguredModelId.Value != Guid.Empty)
            {
                modelsById.TryGetValue(requestBinding.ConfiguredModelId.Value, out model);
            }

            if (model is null && !string.IsNullOrWhiteSpace(requestBinding.RemoteModelId))
            {
                modelsByRemoteModelId.TryGetValue(requestBinding.RemoteModelId.Trim(), out model);
            }

            if (model is null)
            {
                this.ModelState.AddModelError(nameof(requestBindings), $"Purpose '{requestBinding.Purpose}' references an unknown configured model.");
                continue;
            }

            if (requestBinding.Purpose == AiPurpose.EmbeddingDefault)
            {
                if (!model.SupportsEmbedding)
                {
                    this.ModelState.AddModelError(nameof(requestBindings), $"Purpose '{requestBinding.Purpose}' requires an embedding-capable model.");
                }

                if (requestBinding.ProtocolMode is not AiProtocolMode.Auto and not AiProtocolMode.Embeddings)
                {
                    this.ModelState.AddModelError(
                        nameof(requestBindings),
                        $"Purpose '{requestBinding.Purpose}' must use the embeddings protocol or automatic mode.");
                }
            }
            else
            {
                if (!model.SupportsChat)
                {
                    this.ModelState.AddModelError(nameof(requestBindings), $"Purpose '{requestBinding.Purpose}' requires a chat-capable model.");
                }

                if (requestBinding.ProtocolMode != AiProtocolMode.Auto && !model.SupportedProtocolModes.Contains(requestBinding.ProtocolMode))
                {
                    this.ModelState.AddModelError(
                        nameof(requestBindings),
                        $"Model '{model.RemoteModelId}' does not support protocol '{requestBinding.ProtocolMode}'.");
                }
            }

            bindings.Add(
                new AiPurposeBindingDto(
                    requestBinding.Id ?? Guid.Empty,
                    requestBinding.Purpose,
                    model.Id == Guid.Empty ? null : model.Id,
                    model.RemoteModelId,
                    requestBinding.ProtocolMode,
                    requestBinding.IsEnabled));
        }

        return bindings.AsReadOnly();
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
            model.OutputCostPer1MUsd);
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

/// <summary>Authentication settings for one AI connection profile request.</summary>
public sealed record AiConnectionAuthRequest(AiAuthMode Mode, string? ApiKey = null);

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
    decimal? OutputCostPer1MUsd = null);

/// <summary>Purpose binding payload item for create and update flows.</summary>
public sealed record AiPurposeBindingRequest(
    Guid? Id,
    AiPurpose Purpose,
    Guid? ConfiguredModelId = null,
    string? RemoteModelId = null,
    AiProtocolMode ProtocolMode = AiProtocolMode.Auto,
    bool IsEnabled = true);

/// <summary>Request body for creating a provider-neutral AI connection profile.</summary>
public sealed record CreateAiConnectionRequest(
    string DisplayName,
    AiProviderKind ProviderKind,
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
            model.OutputCostPer1MUsd))
        .ToList()
        .AsReadOnly();

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public AiConnectionModelCategory? ModelCategory => null;
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
            model.OutputCostPer1MUsd))
        .ToList()
        .AsReadOnly();

    /// <summary>Legacy compatibility alias for older logging and validation paths.</summary>
    [JsonIgnore]
    public AiConnectionModelCategory? ModelCategory => null;
}

/// <summary>Request body for model discovery against a provider without persisting a profile.</summary>
public sealed record DiscoverModelsRequest(
    AiProviderKind ProviderKind,
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
}
