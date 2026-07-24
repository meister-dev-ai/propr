// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.Providers;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

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
    IAiProviderDriverRegistry providerDrivers) : ControllerBase
{
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

        var writeRequest = this.TryBuildWriteRequest(request);
        if (writeRequest is null)
        {
            return this.ValidationProblem();
        }

        var created = await connections.AddTenantAsync(tenantId, writeRequest, ct);
        return this.CreatedAtAction(nameof(this.List), new { tenantId }, created);
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

        var driver = providerDrivers.GetRequired(existing.ProviderKind);
        var verification = await driver.VerifyAsync(
            new AiConnectionProbeOptionsDto(
                existing.ProviderKind,
                existing.BaseUrl,
                existing.AuthMode,
                existing.Secret,
                existing.DefaultHeaders,
                existing.DefaultQueryParams),
            ct);
        await connections.SaveVerificationAsync(connectionId, verification, ct);
        return this.Ok(verification);
    }

    private IActionResult? RequireTenantAdmin(Guid tenantId)
    {
        return AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
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

        var targetError = providerDrivers.GetRequired(request.ProviderKind)
            .ValidateProbeTarget(new AiProbeTarget(baseUrl, request.Auth.Mode, !string.IsNullOrWhiteSpace(request.Auth.ApiKey)));
        if (targetError is not null)
        {
            this.ModelState.AddModelError(nameof(request.BaseUrl), targetError);
            return null;
        }

        var models = this.MapModels(request.ConfiguredModels);
        if (!this.ModelState.IsValid)
        {
            return null;
        }

        var secret = request.Auth.Mode == AiAuthMode.ApiKey ? request.Auth.ApiKey?.Trim() : null;
        return new AiConnectionWriteRequestDto(
            displayName,
            request.ProviderKind,
            baseUrl,
            request.Auth.Mode,
            request.DiscoveryMode,
            models,
            [],
            request.DefaultHeaders,
            request.DefaultQueryParams,
            secret);
    }

    private IReadOnlyList<AiConfiguredModelDto> MapModels(IReadOnlyList<AiConfiguredModelRequest>? requestModels)
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

            var protocolModes = model.SupportedProtocolModes is { Count: > 0 }
                ? model.SupportedProtocolModes.Distinct().ToList().AsReadOnly()
                : (isEmbedding
                    ? new List<AiProtocolMode> { AiProtocolMode.Auto, AiProtocolMode.Embeddings }.AsReadOnly()
                    : new List<AiProtocolMode> { AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions }.AsReadOnly());

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
                    model.CachedInputCostPer1MUsd));
        }

        return models.AsReadOnly();
    }
}
