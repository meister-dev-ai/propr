// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Resilience;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace MeisterDev.Ai.Providers.LiteLlmAddIn;

/// <summary>
///     A LiteLLM gateway: a proxy the operator runs, speaking the OpenAI wire protocol to whatever sits behind
///     it.
/// </summary>
/// <remarks>
///     <para>
///         The gateway usually runs on a private address inside the operator's own network, which the
///         installation-wide private-egress opt-in exists to reach. What the installation permits an address to
///         reach arrives on the probe target, because this family is constructed with no arguments and has no
///         other way to learn it; the host applies the same rule before this family is asked, and the
///         connect-time address check on every client the host hands out applies it again.
///     </para>
///     <para>
///         Everything it needs at call time comes from the endpoint it is handed: the network through the host's
///         client factory, and the credential from what the connection stored.
///     </para>
/// </remarks>
public sealed class LiteLlmProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "meisterdev/liteLlm";

    /// <summary>This family's OpenAI Responses API shape, as a gateway forwards it.</summary>
    public const string ResponsesProtocol = FamilyKey + ":Responses";

    /// <summary>This family's chat completions shape.</summary>
    public const string ChatCompletionsProtocol = FamilyKey + ":ChatCompletions";

    /// <summary>The single authentication mode this family authenticates with.</summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    /// <summary>
    ///     Usage recorded from a call on this protocol, as the OpenAI client library hands the counts over: a
    ///     4,000-token cache read inside a 4,120-token prompt, and 200 reasoning tokens inside 207 output tokens.
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
    ///         Both totals are already inclusive of their buckets on this protocol, so the mapping returns the
    ///         counters unchanged. Adding either bucket back here would double-count it.
    ///     </para>
    /// </remarks>
    internal const string RecordedUsagePayload =
        "{\"inputTokenCount\":4120,\"outputTokenCount\":207,\"totalTokenCount\":4327,"
        + "\"cachedInputTokenCount\":4000,\"reasoningTokenCount\":200,"
        + "\"additionalCounts\":{\"InputTokenDetails.AudioTokenCount\":0,\"OutputTokenDetails.AudioTokenCount\":0}}";

    /// <summary>
    ///     What this family is, stated once. It declares no configuration fields and no actions: a connection's
    ///     gateway URL, credential and verification state are host columns.
    /// </summary>
    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,
        Label = "LiteLLM",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
            ["LiteLlm"],
            [ApiKeyAuth],
            [ProviderDeclaredProtocolModes.Auto, ResponsesProtocol, ChatCompletionsProtocol, ProviderDeclaredProtocolModes.Embeddings]),

        // A gateway authenticates its callers with a virtual key sent as a bearer token, whatever the upstream
        // provider behind it authenticates with.
        AuthModes =
        [
            new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey]) { Label = "API Key" },
        ],
        ProtocolModes = new ProviderDeclaredProtocolModes(
        [
            // Declared here rather than taken from a shared list. Three families speak this protocol and each
            // states its own shapes, so one adding or withdrawing a surface says nothing about the others.
            ProviderDeclaredProtocolModes.Auto,
            ResponsesProtocol,
            ChatCompletionsProtocol,
            ProviderDeclaredProtocolModes.Embeddings,
        ]),
        ConnectionForm = new ProviderConnectionForm(
            NamePlaceholder: "LiteLLM gateway",
            BaseUrlPlaceholder: "https://gateway.example.com/v1",

            // A gateway renames what it forwards to, so the model identifiers an operator enters are the
            // gateway's own and not the upstream provider's.
            BaseUrlHint: "The gateway URL; models are named as the gateway exposes them."),

        // A gateway runs wherever the operator put it, so this family names no host of its own and the
        // connection's own base URL is what an endpoint restriction is checked against.
        ReachedHostPatterns = [],
        RequestShapeDefaults = new ProviderRequestShapeDefaults(
            RefusedParameterNames: new Dictionary<string, ProviderRequestShapeConstraint>(StringComparer.Ordinal)
            {
                // A reasoning model on this protocol rejects the whole request when it carries a sampling
                // temperature, naming the parameter in the refusal. The name is the wire spelling, which is why
                // it is declared by the family that speaks that wire.
                ["temperature"] = ProviderRequestShapeConstraint.Temperature,
            }),
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
    ///     The address rules are what this family checks: an operator sets the gateway URL, so it is held against
    ///     what the installation permits an address to reach, which the target states. A gateway on a private
    ///     address is therefore taken on an installation that opted into private egress and refused on one that
    ///     did not, which is the case this family exists for. No host is refused on account of who runs it: an
    ///     Azure resource behind a gateway is the gateway's business.
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } malformed)
        {
            return malformed;
        }

        return ProbeTargetChecks.Egress(uri, target) ?? ProbeTargetChecks.RequireApiKey(target, [ApiKeyAuth]);
    }

    /// <inheritdoc />
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        var listing = await LiteLlmModels.ListAsync(endpoint, ct).ConfigureAwait(false);
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
            [.. listing.Models.Select(LiteLlmModels.Describe)]);
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var listing = await LiteLlmModels.ListAsync(endpoint, ct).ConfigureAwait(false);

        return (int)listing.Status >= 400
            ? DriverFailureMapper.Failed(listing.Status, listing.Detail)
            : DriverFailureMapper.Verified(
                $"Verified connectivity for '{endpoint.BaseUrl}'.",
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

        if (this.Options(endpoint) is not { } options)
        {
            return UnreachableClients.ChatClient("LiteLLM");
        }

        if (UsesResponsesApi(protocolMode, model))
        {
            var responses = new OpenAIClient(Credential(endpoint), options);
            return responses.GetResponsesClient().AsIChatClient(model.RemoteModelId);
        }

        var client = new ChatClient(model.RemoteModelId, Credential(endpoint), options);
        return client.AsIChatClient();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing is claimed on this family's behalf. A provider-managed session, a background response and
    ///     prompt caching are affordances of whatever the gateway forwards to, and the gateway states none of
    ///     them, so claiming one would leave a review waiting for a continuation that never arrives.
    /// </remarks>
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        _ = endpoint;
        _ = model;
        _ = protocolMode;

        return ProviderRuntimeCapabilities.None;
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        AiProtocolModeSupport.Require(Declared.Key, this.SupportedProtocolModes, protocolMode);

        // The requested width is not put on the request. This surface takes it as the optional `dimensions`
        // parameter, which the newer embedding models accept and an older deployment refuses outright, so
        // sending it unasked would turn a working profile into a rejected request. The host compares the width
        // of what comes back against the width the connection declares.
        _ = dimensions;

        if (this.Options(endpoint) is not { } options)
        {
            return UnreachableClients.EmbeddingGenerator("LiteLLM");
        }

        var client = new EmbeddingClient(model.RemoteModelId, Credential(endpoint), options);
        return client.AsIEmbeddingGenerator();
    }

    /// <inheritdoc />
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        return LiteLlmFailures.Classify(exception);
    }

    private const string NoModelsDiscovered =
        "No models were discovered from the provider. Manual model entry remains available.";

    private static ApiKeyCredential Credential(ProviderEndpoint endpoint)
    {
        return new ApiKeyCredential(endpoint.Secret ?? string.Empty);
    }

    private static bool UsesResponsesApi(string protocolMode, ProviderModelDescriptor model)
    {
        return ProviderVocabulary.ValuesEqual(protocolMode, ResponsesProtocol)
               || (ProviderVocabulary.ValuesEqual(protocolMode, ProviderDeclaredProtocolModes.Auto)
                   && ProviderVocabulary.Names(model.SupportedProtocolModes, ResponsesProtocol));
    }

    /// <summary>
    ///     The client library's options pointed at this gateway and at the host's transport, or null where the
    ///     host supplied no transport.
    /// </summary>
    /// <param name="endpoint">The endpoint the client is being built for.</param>
    /// <remarks>
    ///     The transport is taken here rather than on the first call because the client library wants one when it
    ///     is constructed. Null rather than a throw, so describing this family still works where calling it
    ///     cannot: the caller hands back a client that refuses instead.
    /// </remarks>
    private OpenAIClientOptions? Options(ProviderEndpoint endpoint)
    {
        if (endpoint.HostContext?.Http is not { } http)
        {
            return null;
        }

        return new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint.BaseUrl, UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(LiteLlmTransport.Client(endpoint, ProviderHttpPurpose.Runtime)),
        };
    }
}
