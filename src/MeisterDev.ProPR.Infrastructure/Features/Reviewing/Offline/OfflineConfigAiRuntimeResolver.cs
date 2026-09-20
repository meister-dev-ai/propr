// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

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

        var model = CreateChatModel(selection.ProviderKind, modelId);
        var binding = new AiPurposeBindingDto(Guid.Empty, purpose, RemoteModelId: modelId);
        var connection = CreateConnection(clientId, selection.ProviderKind, model, binding);
        var client = new ModelDefaultingChatClient(selection.ChatClient, modelId);

        return Task.FromResult<IResolvedAiChatRuntime>(new ResolvedAiChatRuntime(connection, model, binding, client, OfflineCapabilities));
    }

    public Task<IResolvedAiChatRuntime> ResolveChatRuntimeForModelAsync(
        Guid clientId,
        Guid configuredModelId,
        CancellationToken ct = default)
    {
        // The offline harness drives multi-pass union through its diversity arms over the run's shared chat client
        // (the eval path), not the production per-client review-pass list. There is no configured-model registry to
        // resolve a persisted model id against offline, so this path is not supported.
        throw new NotSupportedException("The offline harness resolves multi-pass models through diversity arms, not the per-client review-pass list.");
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

    // A protocol mode of the family the harness is configured against, composed rather than read from that
    // family's declaration because nothing is loaded offline. The family comes from the configuration, so a
    // harness run against Anthropic does not describe its connection as an Azure one.
    private static string Shape(string providerKind, string modeName)
    {
        return ProviderVocabulary.Compose(providerKind, modeName);
    }

    // The offline connection calls one chat client the harness already built, so the shapes on it exist to keep
    // the binding resolvable and never decide a request's format. Auto is what the binding holds; the two named
    // ones are there because a pass that asks for a specific shape has to find it declared.
    private static AiConfiguredModelDto CreateChatModel(string providerKind, string modelId)
    {
        return new AiConfiguredModelDto(
            Guid.Empty,
            modelId,
            modelId,
            [AiOperationKind.Chat],
            [
                ProviderDeclaredProtocolModes.Auto,
                Shape(providerKind, "Responses"),
                Shape(providerKind, "ChatCompletions"),
            ],
            SupportsStructuredOutput: true,
            SupportsToolUse: true,
            Source: AiConfiguredModelSource.Manual);
    }

    private static AiConnectionDto CreateConnection(
        Guid clientId,
        string providerKind,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding)
    {
        return new AiConnectionDto(
            Guid.Empty,
            clientId,
            "offline-harness",
            providerKind,
            "offline",
            Shape(providerKind, "ApiKey"),
            AiDiscoveryMode.ManualOnly,
            true,
            [model],
            [binding],
            AiVerificationResultDto.NeverVerified,
            default,
            default);
    }
}
