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

namespace MeisterDev.Ai.Providers.OpenAiCompatibleAddIn;

/// <summary>
///     Any endpoint that speaks the OpenAI wire protocol, reached at a base URL an operator entered: a vendor
///     API, an aggregator, or a self-hosted server.
/// </summary>
/// <remarks>
///     <para>
///         This is the family for the long tail, so it names no host of its own and refuses no endpoint on
///         account of who runs it. What it does refuse is an address the installation does not permit and a
///         profile with no credential, which are the two things that make a connection unusable rather than
///         unfamiliar.
///     </para>
///     <para>
///         Constructed by the loader with no arguments, so everything it needs at call time comes from the
///         endpoint it is handed: the network through the host's client factory, and the credential from what the
///         connection stored.
///     </para>
/// </remarks>
public sealed class OpenAiCompatibleProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "meisterdev/openAiCompatible";

    /// <summary>The one protocol mode a compatible server can be assumed to serve.</summary>
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
    ///     base URL, credential and verification state are host columns.
    /// </summary>
    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,

        // The base URL is in the label because it is what separates this family from the vendor ones: an
        // operator reaches a server of their own here, wherever it runs.
        Label = "OpenAI-compatible (custom base URL)",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
            ["OpenAiCompatible"],
            [ApiKeyAuth],
            [ProviderDeclaredProtocolModes.Auto, ChatCompletionsProtocol, ProviderDeclaredProtocolModes.Embeddings]),

        // What makes an endpoint OpenAI-compatible includes how it is authenticated: a bearer key. A vendor
        // behind such an endpoint may have an authentication mode of its own, but reaching it through this profile
        // means the compatible surface, and that surface reads a key.
        AuthModes =
        [
            new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey]) { Label = "API Key" },
        ],
        ProtocolModes = new ProviderDeclaredProtocolModes(
        [
            // The Responses API is deliberately absent: it is an OpenAI-specific surface, and assuming it of an
            // arbitrary compatible endpoint turns into a 404 on the first call.
            ProviderDeclaredProtocolModes.Auto,
            ChatCompletionsProtocol,
            ProviderDeclaredProtocolModes.Embeddings,
        ]),
        ConnectionForm = new ProviderConnectionForm(
            NamePlaceholder: "DeepSeek via opencode Zen",
            BaseUrlPlaceholder: "https://opencode.ai/zen/v1",

            // The compatible surface is what this family reaches, so what runs behind the address does not
            // decide whether it belongs here; serving the protocol at that address does.
            BaseUrlHint: "Whatever serves an OpenAI-compatible /chat/completions at this URL, vendor or "
                         + "self-hosted."),

        // This family exists for the long tail, so it names no host of its own: where a connection goes is the
        // operator's base URL, and that is what an endpoint restriction is checked against.
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
    ///     The address rules are the point of this family: an operator sets the base URL, so it is checked
    ///     against what the installation permits an address to reach, which the target states. The host applies
    ///     the same rule before this runs and the connect-time check on every client it hands out applies it
    ///     again, so what is here refuses early rather than being the only thing that refuses.
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
        var listing = await OpenAiCompatibleModels.ListAsync(endpoint, ct).ConfigureAwait(false);
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
            [.. listing.Models.Select(OpenAiCompatibleModels.Describe)]);
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var listing = await OpenAiCompatibleModels.ListAsync(endpoint, ct).ConfigureAwait(false);

        return (int)listing.Status >= 400
            ? DriverFailureMapper.Failed(listing.Status, listing.Detail)
            : DriverFailureMapper.Verified(
                $"Verified connectivity for '{endpoint.BaseUrl}'.",
                listing.Models.Count == 0 ? [NoModelsDiscovered] : []);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The model's declared shapes are narrowed to what this family speaks, so
    ///     <see cref="ProviderDeclaredProtocolModes.Auto" />
    ///     cannot resolve to the Responses API on a server that has no Responses API — a catalog entry
    ///     advertising it is describing the vendor rather than this endpoint.
    /// </remarks>
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
            return UnreachableClients.ChatClient();
        }

        var narrowed = AiProtocolModeSupport.NarrowToSupported(model, this.SupportedProtocolModes);
        var client = new ChatClient(narrowed.RemoteModelId, Credential(endpoint), options);
        return client.AsIChatClient();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing is claimed on this family's behalf. Provider-managed sessions, background responses and prompt
    ///     caching are affordances of one vendor's own surface, and an arbitrary compatible server cannot be
    ///     assumed to implement any of them.
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
            return UnreachableClients.EmbeddingGenerator();
        }

        var client = new EmbeddingClient(model.RemoteModelId, Credential(endpoint), options);
        return client.AsIEmbeddingGenerator();
    }

    /// <inheritdoc />
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        return OpenAiCompatibleFailures.Classify(exception);
    }

    private const string NoModelsDiscovered =
        "No models were discovered from the provider. Manual model entry remains available.";

    private static ApiKeyCredential Credential(ProviderEndpoint endpoint)
    {
        return new ApiKeyCredential(endpoint.Secret ?? string.Empty);
    }

    /// <summary>
    ///     The client library's options pointed at this endpoint and at the host's transport, or null where the
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
            Transport = new HttpClientPipelineTransport(OpenAiCompatibleTransport.Client(endpoint, ProviderHttpPurpose.Runtime)),
        };
    }
}
