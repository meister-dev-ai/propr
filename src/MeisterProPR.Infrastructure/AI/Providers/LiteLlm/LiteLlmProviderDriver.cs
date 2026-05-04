// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.OpenAiCompatible;
using MeisterProPR.Infrastructure.AI.Providers.OpenAi;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI.Providers.LiteLlm;

/// <summary>
///     LiteLLM OpenAI-compatible provider driver.
/// </summary>
public sealed class LiteLlmProviderDriver(OpenAiCompatibleTransport transport) : IAiProviderDriver
{
    private readonly OpenAiProviderDriver _innerDriver = new(transport);

    public AiProviderKind ProviderKind => AiProviderKind.LiteLlm;

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

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        int dimensions)
    {
        return this._innerDriver.CreateEmbeddingGenerator(connection with { ProviderKind = AiProviderKind.LiteLlm }, model, binding, dimensions);
    }
}
