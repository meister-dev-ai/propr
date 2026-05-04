// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Services;

internal static class AiConnectionTestFactory
{
    public static AiConfiguredModelDto CreateChatModel(string remoteModelId, Guid? id = null)
    {
        return new AiConfiguredModelDto(
            id ?? Guid.NewGuid(),
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions],
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
            [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
            "cl100k_base",
            8192,
            dimensions);
    }

    public static AiPurposeBindingDto CreateBinding(
        AiPurpose purpose,
        AiConfiguredModelDto model,
        AiProtocolMode protocolMode = AiProtocolMode.Auto,
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
            AiProviderKind.AzureOpenAi,
            baseUrl,
            secret is null ? AiAuthMode.AzureIdentity : AiAuthMode.ApiKey,
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
}
