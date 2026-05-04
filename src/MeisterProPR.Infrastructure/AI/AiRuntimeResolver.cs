// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.Providers;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Resolves provider-neutral AI runtimes for chat and embedding purposes.
/// </summary>
public sealed class AiRuntimeResolver(
    IAiConnectionRepository aiConnectionRepository,
    IAiProviderDriverRegistry providerDriverRegistry) : IAiRuntimeResolver
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
        return new ResolvedAiChatRuntime(resolved.Connection, resolved.Model, resolved.Binding, client);
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
            generator,
            resolved.Model.TokenizerName,
            resolved.Model.EmbeddingDimensions.Value);
    }
}
