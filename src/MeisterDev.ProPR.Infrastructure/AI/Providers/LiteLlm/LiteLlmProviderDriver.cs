// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.AI.OpenAiCompatible;
using MeisterDev.ProPR.Infrastructure.AI.Providers.OpenAi;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI.Providers.LiteLlm;

/// <summary>
///     LiteLLM OpenAI-compatible provider driver.
/// </summary>
public sealed class LiteLlmProviderDriver(
    OpenAiCompatibleTransport transport,
    IHttpClientFactory httpClientFactory,
    bool allowPrivateEgress,
    bool allowInsecureScheme) : IAiProviderDriver
{
    private readonly OpenAiProviderDriver _innerDriver = new(transport, httpClientFactory, allowPrivateEgress, allowInsecureScheme);

    public AiProviderKind ProviderKind => AiProviderKind.LiteLlm;

    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        // LiteLLM is a generic OpenAI-compatible proxy, so (unlike plain OpenAI) an Azure host is not rejected.
        return AiProbeTargetValidation.ForOpenAiCompatible(target, allowPrivateEgress, allowInsecureScheme, rejectAzureHosts: false);
    }

    public Task<AiModelDiscoveryResultDto> DiscoverModelsAsync(AiConnectionProbeOptionsDto options, CancellationToken ct = default)
    {
        return this._innerDriver.DiscoverModelsAsync(options with { ProviderKind = AiProviderKind.LiteLlm }, ct);
    }

    public Task<AiVerificationResultDto> VerifyAsync(AiConnectionProbeOptionsDto options, CancellationToken ct = default)
    {
        return this._innerDriver.VerifyAsync(options with { ProviderKind = AiProviderKind.LiteLlm }, ct);
    }

    public IChatClient CreateChatClient(AiConnectionDto connection, AiConfiguredModelDto model, AiPurposeBindingDto binding)
    {
        return this._innerDriver.CreateChatClient(connection with { ProviderKind = AiProviderKind.LiteLlm }, model, binding);
    }

    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding)
    {
        _ = connection;
        _ = model;
        _ = binding;

        return new ProviderRuntimeCapabilities(
            false,
            false,
            false,
            false);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        int dimensions)
    {
        return this._innerDriver.CreateEmbeddingGenerator(connection with { ProviderKind = AiProviderKind.LiteLlm }, model, binding, dimensions);
    }
}
