// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

internal static class AiConnectionTestFactory
{
    public const string AzureFamilyKey = "meisterdev/azureOpenAi";

    public const string AzureResponses = AzureFamilyKey + ":Responses";

    public const string AzureChatCompletions = AzureFamilyKey + ":ChatCompletions";

    public const string ApiKeyAuth = AzureFamilyKey + ":ApiKey";

    public const string AzureIdentityAuth = AzureFamilyKey + ":AzureIdentity";

    public static AiConfiguredModelDto CreateChatModel(string remoteModelId, Guid? id = null)
    {
        return new AiConfiguredModelDto(
            id ?? Guid.NewGuid(),
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, AzureResponses, AzureChatCompletions],
            null,
            null,
            null,
            true,
            true);
    }

    public static AiConfiguredModelDto CreateEmbeddingModel(
        string remoteModelId,
        int dimensions = 1536,
        Guid? id = null)
    {
        return new AiConfiguredModelDto(
            id ?? Guid.NewGuid(),
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Embedding],
            [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
            "cl100k_base",
            8192,
            dimensions);
    }

    public static AiPurposeBindingDto CreateBinding(
        AiPurpose purpose,
        AiConfiguredModelDto model,
        string protocolMode = ProviderDeclaredProtocolModes.Auto,
        bool isEnabled = true)
    {
        return new AiPurposeBindingDto(
            Guid.NewGuid(),
            purpose,
            model.Id,
            model.RemoteModelId,
            protocolMode,
            isEnabled);
    }

    public static AiConnectionDto CreateConnection(
        Guid clientId,
        IReadOnlyList<AiConfiguredModelDto>? configuredModels = null,
        IReadOnlyList<AiPurposeBindingDto>? purposeBindings = null,
        string displayName = "Test Connection",
        string baseUrl = "https://api.test.com/",
        bool isActive = true,
        string? secret = "test-key",
        AiVerificationResultDto? verification = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiConnectionDto(
            Guid.NewGuid(),
            clientId,
            displayName,
            AzureFamilyKey,
            baseUrl,
            secret is null ? AzureIdentityAuth : ApiKeyAuth,
            AiDiscoveryMode.ManualOnly,
            isActive,
            configuredModels ?? [],
            purposeBindings ?? [],
            verification ?? AiVerificationResultDto.NeverVerified,
            now,
            now,
            null,
            null,
            secret);
    }

    public static AiConnectionDto CreateChatConnection(
        Guid clientId,
        string modelId = "gpt-4o",
        AiPurpose purpose = AiPurpose.ReviewDefault,
        bool includeBinding = true,
        string displayName = "Test Connection",
        string baseUrl = "https://api.test.com/",
        bool isActive = true,
        string? secret = "test-key")
    {
        var model = CreateChatModel(modelId);
        var bindings = includeBinding ? new[] { CreateBinding(purpose, model) } : [];
        return CreateConnection(clientId, [model], bindings, displayName, baseUrl, isActive, secret);
    }

    /// <summary>
    ///     A resolver that answers every chat purpose with one runtime over the given client. Callers that do not
    ///     care which client a call lands on can leave it unset and get a substitute.
    /// </summary>
    public static IAiRuntimeResolver CreateChatRuntimeResolver(IChatClient? chatClient = null, string modelId = "gpt-4o")
    {
        var model = CreateChatModel(modelId);
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient ?? Substitute.For<IChatClient>());
        runtime.Model.Returns(model);
        runtime.Connection.Returns(CreateConnection(Guid.NewGuid(), [model], [CreateBinding(AiPurpose.ReviewDefault, model)]));
        runtime.Capabilities.Returns(new AgentReviewRuntimeCapabilities(false, false, false, false));

        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>())
            .Returns(runtime);
        return resolver;
    }
}
