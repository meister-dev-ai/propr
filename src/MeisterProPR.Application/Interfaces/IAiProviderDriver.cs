// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Provider-specific driver for discovery, verification, and runtime creation.
/// </summary>
public interface IAiProviderDriver
{
    /// <summary>Gets the provider family handled by this driver.</summary>
    AiProviderKind ProviderKind { get; }

    /// <summary>Discovers provider models using the supplied connection settings.</summary>
    Task<AiModelDiscoveryResultDto> DiscoverModelsAsync(
        AiConnectionProbeOptionsDto options,
        CancellationToken ct = default);

    /// <summary>Verifies the provider connection using the supplied settings.</summary>
    Task<AiVerificationResultDto> VerifyAsync(
        AiConnectionProbeOptionsDto options,
        CancellationToken ct = default);

    /// <summary>Creates a chat client for one resolved model binding.</summary>
    IChatClient CreateChatClient(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding);

    /// <summary>Creates an embedding generator for one resolved model binding.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        int dimensions);
}
