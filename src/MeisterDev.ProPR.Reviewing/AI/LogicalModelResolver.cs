// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Resolves a named logical model for a client into a runtime: role → (client override ?? tenant catalog) →
///     {connection, configured model, effort, protocol} → runtime. The connection is loaded by its explicit id via the
///     global connection lookup, so a tenant-catalog entry resolves without being re-scoped to the requesting client.
///     Because that lookup deliberately ignores scope, the tenant boundary is re-checked explicitly through
///     <see cref="IAiConnectionScopeGuard" /> before a runtime is built, so a mapping can never reach a connection
///     outside the requesting client's tenant.
/// </summary>
public sealed class LogicalModelResolver(
    ILogicalModelCatalogRepository catalog,
    IAiConnectionRepository connections,
    IAiRuntimeFactory runtimeFactory,
    IClientRegistry clients,
    IAiConnectionScopeGuard scopeGuard) : ILogicalModelResolver
{
    private const string ResolutionEventName = "logical_model_resolved";

    public async Task<ResolvedLogicalModelChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        string roleName,
        IProtocolRecorder? recorder = null,
        Guid? protocolId = null,
        CancellationToken ct = default)
    {
        var (mapping, layer) = await this.ResolveMappingAsync(clientId, roleName, ct);

        if (mapping.Capability != AiOperationKind.Chat)
        {
            throw new LogicalModelCapabilityMismatchException(roleName, AiOperationKind.Chat, mapping.Capability);
        }

        var (connection, model) = await this.LoadConnectionAndModelAsync(clientId, roleName, mapping, ct);
        if (!model.SupportsChat)
        {
            throw new InvalidOperationException(
                $"The configured model '{model.RemoteModelId}' behind logical model '{roleName}' does not support chat workloads.");
        }

        var binding = SynthesizeBinding(AiPurpose.ReviewDefault, model, mapping.ProtocolMode);
        var runtime = runtimeFactory.CreateChatRuntime(connection, model, binding, roleName);

        await RecordResolutionAsync(recorder, protocolId, roleName, layer, mapping, ct);
        return new ResolvedLogicalModelChatRuntime(runtime, roleName, layer, mapping.ReasoningEffort);
    }

    public async Task<ResolvedLogicalModelEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        string roleName,
        int? expectedDimensions = null,
        IProtocolRecorder? recorder = null,
        Guid? protocolId = null,
        CancellationToken ct = default)
    {
        var (mapping, layer) = await this.ResolveMappingAsync(clientId, roleName, ct);

        if (mapping.Capability != AiOperationKind.Embedding)
        {
            throw new LogicalModelCapabilityMismatchException(roleName, AiOperationKind.Embedding, mapping.Capability);
        }

        var (connection, model) = await this.LoadConnectionAndModelAsync(clientId, roleName, mapping, ct);
        if (!model.SupportsEmbedding)
        {
            throw new InvalidOperationException($"The configured model '{model.RemoteModelId}' behind logical model '{roleName}' does not support embeddings.");
        }

        if (string.IsNullOrWhiteSpace(model.TokenizerName) || !model.EmbeddingDimensions.HasValue)
        {
            throw new InvalidOperationException(
                $"The configured embedding model '{model.RemoteModelId}' behind logical model '{roleName}' is missing capability metadata.");
        }

        if (expectedDimensions.HasValue && model.EmbeddingDimensions.Value != expectedDimensions.Value)
        {
            throw new InvalidOperationException(
                $"The configured embedding model '{model.RemoteModelId}' behind logical model '{roleName}' returns {model.EmbeddingDimensions.Value} dimensions, but {expectedDimensions.Value} are required.");
        }

        var binding = SynthesizeBinding(AiPurpose.EmbeddingDefault, model, mapping.ProtocolMode);
        var runtime = runtimeFactory.CreateEmbeddingRuntime(
            connection,
            model,
            binding,
            model.TokenizerName,
            model.EmbeddingDimensions.Value,
            roleName);

        await RecordResolutionAsync(recorder, protocolId, roleName, layer, mapping, ct);
        return new ResolvedLogicalModelEmbeddingRuntime(runtime, roleName, layer);
    }

    private static AiPurposeBindingDto SynthesizeBinding(AiPurpose purpose, AiConfiguredModelDto model, AiProtocolMode protocolMode)
    {
        // The logical-model layer replaces purpose-based selection; the binding is a lightweight carrier so the
        // provider driver receives the mapping's protocol mode. The purpose value is informational only here.
        return new AiPurposeBindingDto(Guid.NewGuid(), purpose, model.Id, model.RemoteModelId, protocolMode);
    }

    private async Task<(LogicalModelDto Mapping, LogicalModelLayer Layer)> ResolveMappingAsync(
        Guid clientId,
        string roleName,
        CancellationToken ct)
    {
        var overrides = await catalog.GetClientOverridesAsync(clientId, ct);
        var clientOverride = overrides.FirstOrDefault(m => m.Name == roleName);
        if (clientOverride is not null)
        {
            return (clientOverride, LogicalModelLayer.ClientOverride);
        }

        var tenantEntries = await catalog.GetTenantEntriesForClientAsync(clientId, ct);
        var tenantEntry = tenantEntries.FirstOrDefault(m => m.Name == roleName);
        if (tenantEntry is not null)
        {
            return (tenantEntry, LogicalModelLayer.TenantCatalog);
        }

        throw new LogicalModelNotFoundException(roleName);
    }

    private async Task<(AiConnectionDto Connection, AiConfiguredModelDto Model)> LoadConnectionAndModelAsync(
        Guid clientId,
        string roleName,
        LogicalModelDto mapping,
        CancellationToken ct)
    {
        // Global lookup by connection id — the mapping stores an explicit connection, so a tenant-catalog entry
        // resolves regardless of which client owns that connection.
        var connection = await connections.GetByIdAsync(mapping.ConnectionId, ct)
                         ?? throw new InvalidOperationException(
                             $"Logical model '{roleName}' maps to connection '{mapping.ConnectionId}', which no longer exists.");

        // The lookup above is scope-blind by design, so the tenant boundary is enforced here instead — before the
        // connection's credentials are handed to a driver.
        await this.EnsureConnectionIsInTenantScopeAsync(clientId, roleName, connection, ct);

        var model = connection.ConfiguredModels.FirstOrDefault(m => m.Id == mapping.ConfiguredModelId)
                    ?? throw new InvalidOperationException(
                        $"Logical model '{roleName}' maps to configured model '{mapping.ConfiguredModelId}', which is not present on connection '{mapping.ConnectionId}'.");

        return (connection, model);
    }

    private async Task EnsureConnectionIsInTenantScopeAsync(
        Guid clientId,
        string roleName,
        AiConnectionDto connection,
        CancellationToken ct)
    {
        var tenantId = await clients.GetTenantIdAsync(clientId, ct);
        if (tenantId is not { } requestingTenantId || requestingTenantId == Guid.Empty)
        {
            throw new LogicalModelReferenceInvalidException(
                roleName,
                $"client '{clientId}' has no resolvable tenant, so connection '{connection.Id}' cannot be used.");
        }

        var refusal = await scopeGuard.ValidateAsync(connection, requestingTenantId, ct);
        if (refusal is not null)
        {
            throw new LogicalModelReferenceInvalidException(roleName, refusal);
        }
    }

    private static async Task RecordResolutionAsync(
        IProtocolRecorder? recorder,
        Guid? protocolId,
        string roleName,
        LogicalModelLayer layer,
        LogicalModelDto mapping,
        CancellationToken ct)
    {
        if (recorder is null || protocolId is null)
        {
            return;
        }

        var details = JsonSerializer.Serialize(
            new
            {
                role = roleName,
                resolvedLayer = layer.ToString(),
                capability = mapping.Capability.ToString(),
                connectionId = mapping.ConnectionId,
                configuredModelId = mapping.ConfiguredModelId,
                reasoningEffort = mapping.ReasoningEffort.ToString(),
                protocolMode = mapping.ProtocolMode.ToString(),
            });

        await recorder.RecordLogicalModelResolutionEventAsync(protocolId.Value, ResolutionEventName, details, null, null, ct);
    }
}
