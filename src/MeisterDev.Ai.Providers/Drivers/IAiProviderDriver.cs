// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
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
    ///     The protocol shapes this driver can speak. Declared rather than assumed, because the protocol enum
    ///     names shapes no current driver implements: a driver that stayed silent about this would fall through
    ///     to whatever shape it does speak and put a request on the wire in the wrong format.
    /// </summary>
    IReadOnlyList<AiProtocolMode> SupportedProtocolModes { get; }

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

    /// <summary>
    ///     Decides whether a failed call is worth repeating. This is the driver's own judgement because only the
    ///     driver knows how its transport signals throttling — a provider SDK that reports a rate limit as its own
    ///     exception type rather than an HTTP status can only be understood here.
    /// </summary>
    /// <remarks>
    ///     The default reads the HTTP status, or the absence of a response, and is right for anything speaking
    ///     HTTP with conventional status codes. Override it to recognise SDK-specific signals first, then defer to
    ///     <see cref="DriverFailureMapper.ClassifyRuntimeFailure" /> for the rest; a driver that classifies
    ///     everything itself will get the common cases subtly wrong.
    /// </remarks>
    /// <param name="exception">The exception the call threw.</param>
    ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }
}
