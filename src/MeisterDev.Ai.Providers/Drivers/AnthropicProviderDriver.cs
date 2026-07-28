// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Anthropic's native Messages API.
/// </summary>
/// <remarks>
///     Distinct from reaching Claude through an OpenAI-compatible gateway: the native protocol is where
///     cache-control breakpoints and the thinking block are expressible at all, and where the usage payload
///     reports its cache buckets. The base URL is not pinned to Anthropic's own host — the same protocol is served
///     by gateways and enterprise proxies, and a driver that hard-coded the vendor's domain would refuse them for
///     no reason the protocol requires.
/// </remarks>
public sealed class AnthropicProviderDriver(
    OpenAiCompatibleTransport transport,
    IHttpClientFactory httpClientFactory,
    bool allowPrivateEgress,
    bool allowInsecureScheme) : IAiProviderDriver
{
    /// <inheritdoc />
    public AiProviderKind ProviderKind => AiProviderKind.Anthropic;

    /// <inheritdoc />
    public IReadOnlyList<AiProtocolMode> SupportedProtocolModes { get; } =
        [AiProtocolMode.Auto, AiProtocolMode.AnthropicMessages];

    /// <inheritdoc />
    /// <remarks>
    ///     The host is deliberately not pinned to Anthropic's own domain: the Messages protocol is also served by
    ///     gateways and enterprise proxies, so what is checked is the egress policy and that a key is present in
    ///     the mode Anthropic reads it from.
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } urlError)
        {
            return urlError;
        }

        if (ProbeTargetChecks.Egress(uri, allowPrivateEgress, allowInsecureScheme) is { } egressError)
        {
            return egressError;
        }

        // Anthropic rejects a bearer token and reads x-api-key, so a profile configured for bearer auth would
        // fail on its first call. Saying so here beats a 401 an operator has to interpret.
        if (target.AuthMode != AiAuthMode.XApiKey && target.AuthMode != AiAuthMode.ApiKey)
        {
            return "Anthropic authenticates with an API key sent as 'x-api-key'; choose that authentication mode.";
        }

        return target.HasApiKey ? null : "An API key is required for Anthropic.";
    }

    /// <inheritdoc />
    /// <remarks>Anthropic serves the same <c>/models</c> shape as the OpenAI family, so discovery is shared.</remarks>
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        var result = await transport.DiscoverModelsAsync(AsAnthropicEndpoint(endpoint), ct).ConfigureAwait(false);
        if ((int)result.StatusCode >= 400)
        {
            return new ProviderModelDiscoveryResult(
                "failed",
                true,
                [result.ErrorMessage ?? $"Provider discovery failed with status {(int)result.StatusCode}."],
                []);
        }

        return new ProviderModelDiscoveryResult(
            "succeeded",
            true,
            result.Models.Count == 0 ? ["No models were discovered from the provider. Manual model entry remains available."] : [],
            [.. result.Models.Select(ToDiscoveredModel)]);
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        var result = await transport.DiscoverModelsAsync(AsAnthropicEndpoint(endpoint), ct).ConfigureAwait(false);
        if ((int)result.StatusCode >= 400)
        {
            return DriverFailureMapper.Failed(result.StatusCode, result.ErrorMessage);
        }

        ArgumentNullException.ThrowIfNull(endpoint);
        List<string> warnings = result.Models.Count == 0
            ? ["No models were discovered from the provider. Manual model entry remains available."]
            : [];

        return DriverFailureMapper.Verified($"Verified Anthropic connectivity for '{endpoint.BaseUrl}'.", warnings);
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        AiProtocolModeSupport.Require(this.ProviderKind, this.SupportedProtocolModes, protocolMode);

        return new AnthropicMessagesChatClient(
            httpClientFactory.CreateClient("AiProviderRuntime"),
            AsAnthropicEndpoint(endpoint),
            model);
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        _ = endpoint;
        _ = model;
        _ = protocolMode;

        // Provider-managed sessions and background responses are OpenAI-specific affordances with no Messages-API
        // equivalent, so nothing is claimed on Anthropic's behalf. Prompt caching is claimed because the native
        // client actually marks a breakpoint — going through a proxy is where that gets lost.
        return ProviderRuntimeCapabilities.None with { SupportsPromptCaching = true };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Anthropic offers no embedding models at all, so this is refused rather than left to fail at the first
    ///     call with a provider-worded rejection an operator cannot act on.
    /// </remarks>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions)
    {
        throw new InvalidOperationException("Anthropic does not serve embedding models. Bind the embedding role to a provider that does.");
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Adds the one classification the shared default cannot know: Anthropic answers <c>529 overloaded</c>
    ///     when its own capacity is exhausted, which is the most retryable failure it produces and sits outside
    ///     the range a generic 5xx rule covers.
    /// </remarks>
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is HttpRequestException { StatusCode: (System.Net.HttpStatusCode)529 })
            {
                return ProviderFailureVerdict.Transient("Anthropic reported that it is overloaded (HTTP 529).", null, 529);
            }
        }

        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }

    /// <summary>
    ///     Applies the two things Anthropic requires of every request, whatever the operator configured.
    /// </summary>
    /// <remarks>
    ///     The API version has to be on each call, discovery included. And the credential goes in
    ///     <c>x-api-key</c> — which is the provider's rule, not the operator's choice, so a profile saved with the
    ///     ordinary API-key mode is corrected here rather than left to fail with a 401 that says nothing about
    ///     why.
    /// </remarks>
    private static ProviderEndpoint AsAnthropicEndpoint(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var headers = new Dictionary<string, string>(
            endpoint.DefaultHeaders ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("anthropic-version", AnthropicMessagesChatClient.AnthropicVersion);

        return endpoint with { AuthMode = AiAuthMode.XApiKey, DefaultHeaders = headers };
    }

    private static ProviderDiscoveredModel ToDiscoveredModel(string remoteModelId)
    {
        return new ProviderDiscoveredModel(
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto, AiProtocolMode.AnthropicMessages],
            SupportsStructuredOutput: false,
            SupportsToolUse: true);
    }
}
