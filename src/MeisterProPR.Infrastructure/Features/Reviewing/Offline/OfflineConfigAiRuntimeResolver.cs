// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Offline runtime resolver backed by the harness configuration rather than persisted client AI bindings.
///     When a tiered model selection is active for the current run, it resolves each chat purpose to the
///     configured model (low/medium/high review, triage, ProRV prefilter) reusing the run's shared chat client.
///     When no tiered selection is active, or the purpose is not covered, it throws exactly like the persisted
///     resolver does for a missing binding so callers degrade to their single-model / heuristic fallbacks.
/// </summary>
public sealed class OfflineConfigAiRuntimeResolver(IOfflineTierModelAccessor tierModelAccessor) : IAiRuntimeResolver
{
    private static readonly AgentReviewRuntimeCapabilities OfflineCapabilities = new(false, false, false, false);

    public Task<IResolvedAiChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default)
    {
        var selection = tierModelAccessor.Selection
                        ?? throw NoBinding(purpose);

        var modelId = selection.Tiers.ResolveChatModel(purpose, selection.PrimaryModelId);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw NoBinding(purpose);
        }

        var model = CreateChatModel(modelId);
        var binding = new AiPurposeBindingDto(Guid.Empty, purpose, RemoteModelId: modelId);
        var connection = CreateConnection(clientId, model, binding);
        var client = new ModelDefaultingChatClient(selection.ChatClient, modelId);

        return Task.FromResult<IResolvedAiChatRuntime>(new ResolvedAiChatRuntime(connection, model, binding, client, OfflineCapabilities));
    }

    public Task<IResolvedAiEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        // The offline harness does not execute embeddings (memory and ProCursor embedding paths are stubbed
        // out offline). A configured embedding model is documentation only; surface the same "no binding"
        // failure the persisted resolver would so any unexpected caller degrades rather than mis-embeds.
        throw new InvalidOperationException($"The offline harness does not resolve embedding runtimes (purpose '{purpose}').");
    }

    private static InvalidOperationException NoBinding(AiPurpose purpose)
    {
        return new InvalidOperationException($"No active AI binding is configured for purpose '{purpose}'.");
    }

    private static AiConfiguredModelDto CreateChatModel(string modelId)
    {
        return new AiConfiguredModelDto(
            Guid.Empty,
            modelId,
            modelId,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions],
            SupportsStructuredOutput: true,
            SupportsToolUse: true,
            Source: AiConfiguredModelSource.Manual);
    }

    private static AiConnectionDto CreateConnection(Guid clientId, AiConfiguredModelDto model, AiPurposeBindingDto binding)
    {
        return new AiConnectionDto(
            Guid.Empty,
            clientId,
            "offline-harness",
            AiProviderKind.AzureOpenAi,
            "offline",
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            true,
            [model],
            [binding],
            AiVerificationResultDto.NeverVerified,
            default,
            default);
    }
}
