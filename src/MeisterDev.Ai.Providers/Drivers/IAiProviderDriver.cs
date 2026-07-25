// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Provider-specific driver for discovery, verification, and runtime creation.
/// </summary>
public interface IAiProviderDriver
{
    /// <summary>Gets the provider family handled by this driver.</summary>
    AiProviderKind ProviderKind { get; }

    /// <summary>
    ///     Validates a probe/verify target against this provider's base-URL, SSRF-egress, and auth-shape rules.
    ///     Returns a user-facing error message when the target is rejected, or <c>null</c> when it is acceptable.
    /// </summary>
    string? ValidateProbeTarget(AiProbeTarget target);

    /// <summary>Discovers provider models using the supplied connection settings.</summary>
    Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default);

    /// <summary>Verifies the provider connection using the supplied settings.</summary>
    Task<ProviderVerificationResult> VerifyAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default);

    /// <summary>Creates a chat client for one resolved model binding.</summary>
    IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode);

    /// <summary>Gets session-related chat runtime capabilities for one resolved model binding.</summary>
    ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode);

    /// <summary>Creates an embedding generator for one resolved model binding.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions);
}
