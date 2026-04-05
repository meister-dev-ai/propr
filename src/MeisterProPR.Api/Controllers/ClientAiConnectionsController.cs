// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages per-client AI connection configurations.</summary>
[ApiController]
public sealed partial class ClientAiConnectionsController(
    IAiConnectionRepository aiConnections,
    IAiChatClientFactory aiChatClientFactory,
    ILogger<ClientAiConnectionsController> logger) : ControllerBase
{
    private static readonly StringComparer ModelNameComparer = StringComparer.OrdinalIgnoreCase;

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection {ConnectionId} created for client {ClientId}")]
    private static partial void LogConnectionCreated(ILogger logger, Guid connectionId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection {ConnectionId} deleted for client {ClientId}")]
    private static partial void LogConnectionDeleted(ILogger logger, Guid connectionId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection {ConnectionId} activated with model {Model} for client {ClientId}")]
    private static partial void LogConnectionActivated(ILogger logger, Guid connectionId, string model, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI connection {ConnectionId} deactivated for client {ClientId}")]
    private static partial void LogConnectionDeactivated(ILogger logger, Guid connectionId, Guid clientId);

    /// <summary>Validates that the caller has ClientAdministrator access for the specified client (or is a global admin).</summary>
    /// <returns>Null when access is granted; an <see cref="IActionResult"/> to return when denied.</returns>
    private IActionResult? AuthorizeClientAccessAsync(Guid clientId)
    {
        return AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
    }

    private static IReadOnlyList<string> NormalizeModels(IReadOnlyList<string> models)
    {
        return models
            .Select(model => model.Trim())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(ModelNameComparer)
            .ToList()
            .AsReadOnly();
    }

    private static IReadOnlyList<AiConnectionModelCapabilityDto> NormalizeModelCapabilities(
        IReadOnlyList<AiConnectionModelCapabilityRequest> modelCapabilities)
    {
        return modelCapabilities
            .Select(capability => new AiConnectionModelCapabilityDto(
                capability.ModelName.Trim(),
                capability.TokenizerName.Trim(),
                capability.MaxInputTokens,
                capability.EmbeddingDimensions,
                capability.InputCostPer1MUsd,
                capability.OutputCostPer1MUsd))
            .ToList()
            .AsReadOnly();
    }

    private bool ValidateModelCapabilities(
        IReadOnlyList<string> models,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities,
        bool requireComplete,
        string memberName)
    {
        var modelSet = models.ToHashSet(ModelNameComparer);
        var seenModels = new HashSet<string>(ModelNameComparer);

        foreach (var capability in modelCapabilities ?? [])
        {
            if (string.IsNullOrWhiteSpace(capability.ModelName))
            {
                this.ModelState.AddModelError(memberName, "Each model capability must include modelName.");
                continue;
            }

            if (!modelSet.Contains(capability.ModelName))
            {
                this.ModelState.AddModelError(
                    memberName,
                    $"Model capability '{capability.ModelName}' does not match any configured model.");
            }

            if (!seenModels.Add(capability.ModelName))
            {
                this.ModelState.AddModelError(
                    memberName,
                    $"Model capability '{capability.ModelName}' is duplicated.");
            }

            if (!EmbeddingTokenizerRegistry.IsSupported(capability.TokenizerName))
            {
                this.ModelState.AddModelError(
                    memberName,
                    $"Tokenizer '{capability.TokenizerName}' is not supported.");
            }

            if (capability.MaxInputTokens <= 0)
            {
                this.ModelState.AddModelError(memberName, "MaxInputTokens must be greater than zero.");
            }

            if (capability.EmbeddingDimensions is < 64 or > 4096)
            {
                this.ModelState.AddModelError(memberName, "EmbeddingDimensions must be between 64 and 4096.");
            }

            if (capability.InputCostPer1MUsd.HasValue && capability.InputCostPer1MUsd.Value < 0)
            {
                this.ModelState.AddModelError(memberName, "InputCostPer1MUsd must be greater than or equal to zero when provided.");
            }

            if (capability.OutputCostPer1MUsd.HasValue && capability.OutputCostPer1MUsd.Value < 0)
            {
                this.ModelState.AddModelError(memberName, "OutputCostPer1MUsd must be greater than or equal to zero when provided.");
            }
        }

        if (requireComplete)
        {
            foreach (var model in models)
            {
                if (!seenModels.Contains(model))
                {
                    this.ModelState.AddModelError(
                        memberName,
                        $"Embedding connections must define capability metadata for model '{model}'.");
                }
            }
        }

        return this.ModelState.IsValid;
    }

    /// <summary>Lists all AI connections for the specified client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of AI connections (API keys are never returned).</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not own this client.</response>
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

        var connections = await aiConnections.GetByClientAsync(clientId, ct);
        return this.Ok(connections);
    }

    /// <summary>Creates a new AI connection for the specified client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">AI connection details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">AI connection created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not own this client.</response>
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

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200)
        {
            this.ModelState.AddModelError(nameof(request.DisplayName), "displayName is required and must be ≤200 characters.");
            return this.ValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(request.EndpointUrl) || request.EndpointUrl.Length > 500 ||
            !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out _))
        {
            this.ModelState.AddModelError(nameof(request.EndpointUrl), "endpointUrl is required, must be a valid absolute URL, and ≤500 characters.");
            return this.ValidationProblem();
        }

        if (request.Models is null || request.Models.Count == 0)
        {
            this.ModelState.AddModelError(nameof(request.Models), "models must be a non-empty array.");
            return this.ValidationProblem();
        }

        var normalizedModels = NormalizeModels(request.Models);
        if (normalizedModels.Count == 0)
        {
            this.ModelState.AddModelError(nameof(request.Models), "models must contain at least one non-empty value.");
            return this.ValidationProblem();
        }

        IReadOnlyList<AiConnectionModelCapabilityDto>? normalizedCapabilities = null;
        if (request.ModelCapabilities is not null)
        {
            normalizedCapabilities = NormalizeModelCapabilities(request.ModelCapabilities);
        }

        if (!this.ValidateModelCapabilities(
                normalizedModels,
                normalizedCapabilities,
                request.ModelCategory == AiConnectionModelCategory.Embedding,
                nameof(request.ModelCapabilities)))
        {
            return this.ValidationProblem();
        }

        var connection = await aiConnections.AddAsync(
            clientId,
            request.DisplayName,
            request.EndpointUrl,
            normalizedModels,
            request.ApiKey,
            normalizedCapabilities,
            request.ModelCategory,
            ct);

        LogConnectionCreated(logger, connection.Id, clientId);

        return this.CreatedAtAction(
            nameof(this.GetAiConnections),
            new { clientId },
            connection);
    }

    /// <summary>Updates non-null fields of an AI connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">AI connection identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">AI connection updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">AI connection not found.</response>
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

        // Validate that the connection belongs to the specified client.
        var existing = await aiConnections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.ClientId != clientId)
        {
            return this.NotFound();
        }

        if (request.DisplayName is not null && (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200))
        {
            this.ModelState.AddModelError(nameof(request.DisplayName), "displayName must be non-empty and ≤200 characters when provided.");
            return this.ValidationProblem();
        }

        if (request.EndpointUrl is not null &&
            (string.IsNullOrWhiteSpace(request.EndpointUrl) || request.EndpointUrl.Length > 500 ||
             !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out _)))
        {
            this.ModelState.AddModelError(nameof(request.EndpointUrl), "endpointUrl must be a valid absolute URL and ≤500 characters when provided.");
            return this.ValidationProblem();
        }

        if (request.Models is not null && request.Models.Count == 0)
        {
            this.ModelState.AddModelError(nameof(request.Models), "models must be a non-empty array when provided.");
            return this.ValidationProblem();
        }

        var normalizedModels = request.Models is null ? existing.Models : NormalizeModels(request.Models);
        if (normalizedModels.Count == 0)
        {
            this.ModelState.AddModelError(nameof(request.Models), "models must contain at least one non-empty value.");
            return this.ValidationProblem();
        }

        if (!string.IsNullOrWhiteSpace(existing.ActiveModel) &&
            !normalizedModels.Contains(existing.ActiveModel, ModelNameComparer))
        {
            this.ModelState.AddModelError(nameof(request.Models), "models must include the currently active model.");
            return this.ValidationProblem();
        }

        IReadOnlyList<AiConnectionModelCapabilityDto>? normalizedCapabilities = existing.ModelCapabilities;
        if (request.ModelCapabilities is not null)
        {
            normalizedCapabilities = NormalizeModelCapabilities(request.ModelCapabilities);
        }

        if (!this.ValidateModelCapabilities(
                normalizedModels,
                normalizedCapabilities,
                existing.ModelCategory == AiConnectionModelCategory.Embedding,
                nameof(request.ModelCapabilities)))
        {
            return this.ValidationProblem();
        }

        var updated = await aiConnections.UpdateAsync(
            connectionId,
            request.DisplayName,
            request.EndpointUrl,
            request.Models is null ? null : normalizedModels,
            request.ApiKey,
            request.ModelCapabilities is null ? null : normalizedCapabilities,
            ct);

        if (!updated)
        {
            return this.NotFound();
        }

        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        return this.Ok(refreshed);
    }

    /// <summary>Deletes an AI connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">AI connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">AI connection deleted.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">AI connection not found.</response>
    [HttpDelete("clients/{clientId:guid}/ai-connections/{connectionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAiConnection(
        Guid clientId,
        Guid connectionId,
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

        await aiConnections.DeleteAsync(connectionId, ct);
        LogConnectionDeleted(logger, connectionId, clientId);
        return this.NoContent();
    }

    /// <summary>Activates the specified AI connection with the given model deployment.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">AI connection identifier.</param>
    /// <param name="request">Activation request containing the model to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">AI connection activated.</response>
    /// <response code="400">Model not in the connection's model list.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">AI connection not found.</response>
    [HttpPost("clients/{clientId:guid}/ai-connections/{connectionId:guid}/activate")]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateAiConnection(
        Guid clientId,
        Guid connectionId,
        [FromBody] ActivateAiConnectionRequest request,
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

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            this.ModelState.AddModelError(nameof(request.Model), "model is required.");
            return this.ValidationProblem();
        }

        var activated = await aiConnections.ActivateAsync(connectionId, request.Model, ct);
        if (!activated)
        {
            // ActivateAsync returns false when model not in list.
            return this.BadRequest(new { error = $"Model '{request.Model}' is not in the connection's model list." });
        }

        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        LogConnectionActivated(logger, connectionId, request.Model, clientId);
        return this.Ok(refreshed);
    }

    /// <summary>Deactivates the specified AI connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">AI connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">AI connection deactivated.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">AI connection not found.</response>
    [HttpPost("clients/{clientId:guid}/ai-connections/{connectionId:guid}/deactivate")]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateAiConnection(
        Guid clientId,
        Guid connectionId,
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

        await aiConnections.DeactivateAsync(connectionId, ct);
        var refreshed = await aiConnections.GetByIdAsync(connectionId, ct);
        LogConnectionDeactivated(logger, connectionId, clientId);
        return this.Ok(refreshed);
    }

    /// <summary>
    ///     Probes the given endpoint URL with the supplied API key and returns the list of available
    ///     model deployment names. Returns an empty list when the endpoint is unreachable or returns
    ///     no deployments — the caller should surface this as a warning, not an error.
    /// </summary>
    /// <param name="clientId">Client identifier (used for auth).</param>
    /// <param name="request">Endpoint URL and API key to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of model deployment names (may be empty).</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not own this client.</response>
    [HttpPost("clients/{clientId:guid}/ai-connections/discover-models")]
    [ProducesResponseType(typeof(DiscoverModelsResponse), StatusCodes.Status200OK)]
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

        if (string.IsNullOrWhiteSpace(request.EndpointUrl) ||
            !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out _))
        {
            this.ModelState.AddModelError(nameof(request.EndpointUrl), "endpointUrl must be a valid absolute URL.");
            return this.ValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            this.ModelState.AddModelError(nameof(request.ApiKey), "apiKey is required.");
            return this.ValidationProblem();
        }

        var models = await aiChatClientFactory.ProbeDeploymentsAsync(request.EndpointUrl, request.ApiKey, ct);
        return this.Ok(new DiscoverModelsResponse(models));
    }
}

/// <summary>Request body for creating a new AI connection.</summary>
public sealed record CreateAiConnectionRequest(
    string DisplayName,
    string EndpointUrl,
    IReadOnlyList<string> Models,
    string? ApiKey = null,
    IReadOnlyList<AiConnectionModelCapabilityRequest>? ModelCapabilities = null,
    AiConnectionModelCategory? ModelCategory = null);

/// <summary>Request body for updating an AI connection. All fields are optional — omit to leave unchanged.</summary>
public sealed record UpdateAiConnectionRequest(
    string? DisplayName = null,
    string? EndpointUrl = null,
    IReadOnlyList<string>? Models = null,
    string? ApiKey = null,
    IReadOnlyList<AiConnectionModelCapabilityRequest>? ModelCapabilities = null);

/// <summary>One per-deployment embedding capability payload item for create or update requests.</summary>
public sealed record AiConnectionModelCapabilityRequest(
    string ModelName,
    string TokenizerName,
    int MaxInputTokens,
    int EmbeddingDimensions,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null);

/// <summary>Request body for activating an AI connection with a specific model.</summary>
public sealed record ActivateAiConnectionRequest(string Model);

/// <summary>Request body for probing an AI endpoint to discover available model deployments.</summary>
public sealed record DiscoverModelsRequest(string EndpointUrl, string ApiKey);

/// <summary>Response from the discover-models probe.</summary>
public sealed record DiscoverModelsResponse(IReadOnlyList<string> Models);
