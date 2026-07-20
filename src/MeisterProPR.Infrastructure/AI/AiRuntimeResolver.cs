// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI.Providers;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Resolves provider-neutral AI runtimes for chat and embedding purposes. When a budget scope accessor is
///     available, the resolved chat client and embedding generator are wrapped so every model call is metered and
///     gated against the active review job's USD hard cap.
/// </summary>
public sealed class AiRuntimeResolver(
    IAiConnectionRepository aiConnectionRepository,
    IAiProviderDriverRegistry providerDriverRegistry,
    IBudgetScopeAccessor? budgetScopeAccessor = null) : IAiRuntimeResolver
{
    public async Task<IResolvedAiChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default)
    {
        var resolved = await aiConnectionRepository.GetActiveBindingForPurposeAsync(clientId, purpose, ct)
                       ?? throw new InvalidOperationException($"No active AI binding is configured for purpose '{purpose}'.");

        if (!resolved.Model.SupportsChat)
        {
            throw new InvalidOperationException($"The configured model '{resolved.Model.RemoteModelId}' does not support chat workloads.");
        }

        var driver = providerDriverRegistry.GetRequired(resolved.Connection.ProviderKind);
        var client = driver.CreateChatClient(resolved.Connection, resolved.Model, resolved.Binding);
        var capabilities = driver.GetChatRuntimeCapabilities(resolved.Connection, resolved.Model, resolved.Binding);
        return new ResolvedAiChatRuntime(resolved.Connection, resolved.Model, resolved.Binding, this.WrapChatClient(client, resolved.Model), capabilities);
    }

    public async Task<IResolvedAiChatRuntime> ResolveChatRuntimeForModelAsync(
        Guid clientId,
        Guid configuredModelId,
        CancellationToken ct = default)
    {
        var resolved = await aiConnectionRepository.GetModelBindingAsync(clientId, configuredModelId, ct)
                       ?? throw new InvalidOperationException($"No chat-capable configured model '{configuredModelId}' is available for the client.");

        if (!resolved.Model.SupportsChat)
        {
            throw new InvalidOperationException($"The configured model '{resolved.Model.RemoteModelId}' does not support chat workloads.");
        }

        var driver = providerDriverRegistry.GetRequired(resolved.Connection.ProviderKind);
        var client = driver.CreateChatClient(resolved.Connection, resolved.Model, resolved.Binding);
        var capabilities = driver.GetChatRuntimeCapabilities(resolved.Connection, resolved.Model, resolved.Binding);
        return new ResolvedAiChatRuntime(resolved.Connection, resolved.Model, resolved.Binding, this.WrapChatClient(client, resolved.Model), capabilities);
    }

    public async Task<IResolvedAiEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        var resolved = await aiConnectionRepository.GetActiveBindingForPurposeAsync(clientId, purpose, ct)
                       ?? throw new InvalidOperationException($"No active AI binding is configured for purpose '{purpose}'.");

        if (!resolved.Model.SupportsEmbedding)
        {
            throw new InvalidOperationException($"The configured model '{resolved.Model.RemoteModelId}' does not support embeddings.");
        }

        if (string.IsNullOrWhiteSpace(resolved.Model.TokenizerName) || !resolved.Model.EmbeddingDimensions.HasValue)
        {
            throw new InvalidOperationException($"The configured embedding model '{resolved.Model.RemoteModelId}' is missing capability metadata.");
        }

        if (expectedDimensions.HasValue && resolved.Model.EmbeddingDimensions.Value != expectedDimensions.Value)
        {
            throw new InvalidOperationException(
                $"The configured embedding model '{resolved.Model.RemoteModelId}' returns {resolved.Model.EmbeddingDimensions.Value} dimensions, but {expectedDimensions.Value} are required.");
        }

        var driver = providerDriverRegistry.GetRequired(resolved.Connection.ProviderKind);
        var generator = driver.CreateEmbeddingGenerator(
            resolved.Connection,
            resolved.Model,
            resolved.Binding,
            resolved.Model.EmbeddingDimensions.Value);

        return new ResolvedAiEmbeddingRuntime(
            resolved.Connection,
            resolved.Model,
            resolved.Binding,
            this.WrapEmbeddingGenerator(generator, resolved.Model),
            resolved.Model.TokenizerName,
            resolved.Model.EmbeddingDimensions.Value);
    }

    private static ModelPricing ToPricing(AiConfiguredModelDto model)
    {
        return new ModelPricing(model.InputCostPer1MUsd, model.OutputCostPer1MUsd, model.CachedInputCostPer1MUsd);
    }

    private IChatClient WrapChatClient(IChatClient client, AiConfiguredModelDto model)
    {
        return budgetScopeAccessor is null
            ? client
            : new BudgetEnforcingChatClient(client, budgetScopeAccessor, ToPricing(model));
    }

    private IEmbeddingGenerator<string, Embedding<float>> WrapEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        AiConfiguredModelDto model)
    {
        return budgetScopeAccessor is null
            ? generator
            : new BudgetEnforcingEmbeddingGenerator(generator, budgetScopeAccessor, ToPricing(model));
    }
}
