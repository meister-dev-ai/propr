// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.AnthropicAddIn;

/// <summary>
///     Anthropic's native Messages API.
/// </summary>
/// <remarks>
///     <para>
///         Distinct from reaching Claude through an OpenAI-compatible gateway: the native protocol is where
///         cache-control breakpoints and the thinking block are expressible at all, and where the usage payload
///         reports its cache buckets. The base URL is not pinned to Anthropic's own host — the same protocol is
///         served by gateways and enterprise proxies, and a family that hard-coded the vendor's domain would
///         refuse them for no reason the protocol requires.
///     </para>
///     <para>
///         Constructed by the loader with no arguments, so everything it needs at call time comes from the
///         endpoint it is handed: the network through the host's client factory, and the credential from what the
///         connection stored.
///     </para>
/// </remarks>
public sealed class AnthropicProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "meisterdev/anthropic";

    /// <summary>The protocol mode this family owns: Anthropic's native Messages API.</summary>
    public const string MessagesProtocol = FamilyKey + ":AnthropicMessages";

    /// <summary>
    ///     The one authentication mode this family reads: a single API key. Where that key goes on the wire is this
    ///     family's own business — Anthropic reads it from x-api-key and rejects a bearer token — and not
    ///     something an operator chooses, so it is not a second shape.
    /// </summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    private const string NoModelsDiscovered =
        "No models were discovered from the provider. Manual model entry remains available.";

    /// <summary>
    ///     Usage recorded from a call served largely from cache, as Anthropic reports the counts: 120 real prompt
    ///     tokens beside a 4,000-token cache read and a 50-token cache write.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The driver checks replay this through <c>ReadUsage</c> and assert the counter relationship on the
    ///         result: the input total covers both cache buckets and the output total covers the reasoning
    ///         portion. The host bills the input total less the two cache buckets, floored at zero, so a mapping
    ///         that returns a vendor's counts unchanged where the vendor reports input exclusive of them bills
    ///         the whole prompt at nothing. The payload is recorded from the vendor and not written by hand,
    ///         because a synthesised one would restate the assumption of whoever wrote the mapping.
    ///     </para>
    ///     <para>
    ///         Anthropic reports its prompt count exclusive of both cache buckets, so the mapping adds them back
    ///         in. This payload is what it looks like before it does.
    ///     </para>
    /// </remarks>
    internal const string RecordedUsagePayload =
        "{\"inputTokenCount\":120,\"outputTokenCount\":207,\"cachedInputTokenCount\":4000,"
        + "\"additionalCounts\":{\"cache_creation_input_tokens\":50}}";

    /// <summary>
    ///     What this family is, stated once. It declares no configuration fields and no actions: a connection's
    ///     base URL, credential and verification state are host columns.
    /// </summary>
    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,

        // Marked as the native surface, because Claude models are also reached through Bedrock and through a
        // gateway, and those are configured on other families.
        Label = "Anthropic (native)",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
            ["Anthropic"],
            [ApiKeyAuth],
            [MessagesProtocol]),

        // One mode, because an authentication mode states what an operator supplies and this family reads one API
        // key. The header it travels in is applied by the client on every request whatever the connection says,
        // so a second mode naming that header would offer a choice with no consequence.
        AuthModes = [new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey]) { Label = "API Key" }],
        ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, MessagesProtocol]),
        ConnectionForm = new ProviderConnectionForm(
            NamePlaceholder: "Claude (native)",
            BaseUrlPlaceholder: "https://api.anthropic.com/v1",

            // What this family needs from the address is the protocol, not the vendor, which is why it names no
            // reached host of its own.
            BaseUrlHint: "Any host that speaks the Messages API works, including a gateway in front of it."),

        // The Messages protocol is also served by gateways and enterprise proxies, so this family names no host
        // of its own and the connection's own base URL is what an endpoint restriction is checked against.
        ReachedHostPatterns = [],
        ConformanceInputs = new ProviderConformanceInputs(ApiKeyAuth, RecordedUsagePayload: RecordedUsagePayload),
    };

    /// <inheritdoc />
    public ProviderDeclaration Declaration => Declared;


    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProtocolModes => Declared.ProtocolModes.Supported;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedAuthModes => Declared.SupportedAuthModes;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
        Declared.CredentialFields;

    /// <inheritdoc />
    /// <remarks>
    ///     The host is not pinned to Anthropic's own domain: the Messages protocol is also served by gateways and
    ///     enterprise proxies, so what is checked is the address the installation permits and that a key is
    ///     present in a mode Anthropic reads it from. What the installation permits arrives on the target,
    ///     because this family is constructed with no arguments and has no other way to learn it.
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } urlError)
        {
            return urlError;
        }

        if (ProbeTargetChecks.Egress(uri, target) is { } egressError)
        {
            return egressError;
        }

        // A profile naming an authentication mode this family does not read would fail on its first call. Saying so
        // here beats a 401 an operator has to interpret.
        if (!ProviderVocabulary.Names([ApiKeyAuth], target.AuthMode))
        {
            return "Anthropic authenticates with an API key; choose the API Key authentication mode.";
        }

        return target.HasApiKey ? null : "An API key is required for Anthropic.";
    }

    /// <inheritdoc />
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        var listing = await AnthropicModels.ListAsync(endpoint, ct).ConfigureAwait(false);
        if ((int)listing.Status >= 400)
        {
            return new ProviderModelDiscoveryResult(
                "failed",
                true,
                [listing.Detail ?? $"Provider discovery failed with status {(int)listing.Status}."],
                []);
        }

        return new ProviderModelDiscoveryResult(
            "succeeded",
            true,
            listing.Models.Count == 0 ? [NoModelsDiscovered] : [],
            [.. listing.Models.Select(AnthropicModels.Describe)]);
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var listing = await AnthropicModels.ListAsync(endpoint, ct).ConfigureAwait(false);

        return (int)listing.Status >= 400
            ? DriverFailureMapper.Failed(listing.Status, listing.Detail)
            : DriverFailureMapper.Verified(
                $"Verified Anthropic connectivity for '{endpoint.BaseUrl}'.",
                listing.Models.Count == 0 ? [NoModelsDiscovered] : []);
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        AiProtocolModeSupport.Require(Declared.Key, this.SupportedProtocolModes, protocolMode);

        return new AnthropicMessagesChatClient(endpoint.HostContext?.Http, endpoint, model);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Anthropic reports <c>input_tokens</c> exclusive of both cache buckets, so both are added back into the
    ///     input total. Without that the host bills the input total less the buckets, which for a call served
    ///     largely from cache floors at zero and charges the real prompt at nothing. Anthropic reports no separate
    ///     reasoning counter: its thinking tokens are already inside the output count.
    /// </remarks>
    public ProviderTokenUsage ReadUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return ProviderTokenUsage.Missing;
        }

        var cacheRead = usage.CachedInputTokenCount ?? 0;
        var cacheWrite = usage.AdditionalCounts?.TryGetValue(AnthropicUsageCounters.CacheCreation, out var written) == true
            ? written
            : 0;

        return new ProviderTokenUsage(
            (usage.InputTokenCount ?? 0) + cacheRead + cacheWrite,
            usage.OutputTokenCount ?? 0,
            cacheRead,
            cacheWrite);
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
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
        string protocolMode,
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

        foreach (var candidate in DriverFailureMapper.Unwind(exception))
        {
            if (candidate is HttpRequestException { StatusCode: (System.Net.HttpStatusCode)529 })
            {
                return ProviderFailureVerdict.Transient("Anthropic reported that it is overloaded (HTTP 529).", null, 529);
            }
        }

        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }
}
