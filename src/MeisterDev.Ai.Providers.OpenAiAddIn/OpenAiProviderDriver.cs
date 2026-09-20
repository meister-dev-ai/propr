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

namespace MeisterDev.Ai.Providers.OpenAiAddIn;

/// <summary>
///     OpenAI's own API, reached at the vendor's endpoint with a key.
/// </summary>
/// <remarks>
///     <para>
///         What separates this family from the compatible one is the endpoint it is pinned to and the Responses
///         API it can speak there. An Azure-hosted resource is refused rather than served: it authenticates
///         differently and can use a managed identity instead of a key, so a profile stored here would either
///         fail on its first call or lock the operator out of the keyless option.
///     </para>
///     <para>
///         Constructed by the loader with no arguments, so everything it needs at call time comes from the
///         endpoint it is handed: the network through the host's client factory, and the credential from what the
///         connection stored.
///     </para>
/// </remarks>
public sealed class OpenAiProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "meisterdev/openAi";

    /// <summary>This family's OpenAI Responses API shape.</summary>
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

    private const string NoModelsDiscovered =
        "No models were discovered from the provider. Manual model entry remains available.";

    /// <summary>
    ///     The hostname suffixes an Azure OpenAI or Azure AI Foundry resource is reached at.
    /// </summary>
    /// <remarks>
    ///     Held here because this is the one thing this family needs to know about those hosts: that they belong
    ///     to another family and are refused rather than served. The Azure family declares the same suffixes as
    ///     the hosts it reaches, in its own assembly, because a family loaded from a directory shares no type
    ///     with the host or with another family.
    /// </remarks>
    private static readonly string[] AzureAiHostSuffixes =
    [
        ".openai.azure.com",
        ".services.ai.azure.com",
        ".cognitiveservices.azure.com",
    ];

    /// <summary>
    ///     What this family is, stated once. It declares no configuration fields and no actions: a connection's
    ///     base URL, credential and verification state are host columns, and nothing about this family is
    ///     configured beyond them.
    /// </summary>
    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,

        // Named apart from the Azure family in the label as well as the key, because the two are told apart by
        // where the endpoint is hosted and an operator picking one is choosing between them.
        Label = "OpenAI (non-Azure)",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
            ["OpenAi"],
            [ApiKeyAuth],
            [ProviderDeclaredProtocolModes.Auto, ResponsesProtocol, ChatCompletionsProtocol, ProviderDeclaredProtocolModes.Embeddings]),

        // The client library is built from an ApiKeyCredential and sends it as a bearer token, which is the one
        // place this family can put a credential.
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
            NamePlaceholder: "OpenAI (prod)",
            BaseUrlPlaceholder: "https://api.openai.com/v1",

            // The mistake this hint exists for: an Azure resource endpoint pasted here reaches a family that
            // refuses Azure hosts, and the refusal names the host rather than the family it belongs to.
            BaseUrlHint: "Azure-hosted endpoints, including Azure AI Foundry OpenAI endpoints, belong under "
                         + "Azure OpenAI / AI Foundry."),

        // The vendor endpoint, and that separates this family from the compatible one: an endpoint
        // anywhere else is configured as OpenAI-compatible.
        ReachedHostPatterns = ["api.openai.com"],
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
    ///     An Azure-hosted endpoint is refused rather than accepted: it authenticates differently, and the Azure
    ///     family is the one that can use a managed identity instead of a key. What the installation permits an
    ///     address to reach arrives on the target, because this family is constructed with no arguments and has
    ///     no other way to learn it.
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } urlError)
        {
            return urlError;
        }

        if (IsAzureAiHost(uri.Host))
        {
            return "Azure-hosted OpenAI endpoints, including Azure AI Foundry OpenAI endpoints, must use "
                   + "providerKind 'azureOpenAi' instead of 'openAi'.";
        }

        // The vendor endpoint is what separates this family from the compatible one, and the declaration names
        // it. Read from that list rather than from a literal here, so the refusal and the declared reach cannot
        // drift apart. Without this an operator could point the family at any host and the vendor key would go
        // there, while the tenant's endpoint restriction went on being checked against 'api.openai.com'.
        if (!ProviderHostPattern.DoesAnyPatternMatchHost(Declared.ReachedHostPatterns, uri.Host))
        {
            return $"An OpenAI connection reaches {string.Join(", ", Declared.ReachedHostPatterns)}. Configure an "
                   + "endpoint anywhere else as OpenAI-compatible.";
        }

        return ProbeTargetChecks.Egress(uri, target) ?? ProbeTargetChecks.RequireApiKey(target, [ApiKeyAuth]);
    }

    /// <inheritdoc />
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        var listing = await OpenAiModels.ListAsync(endpoint, ct).ConfigureAwait(false);
        if ((int)listing.Status >= 400)
        {
            return new ProviderModelDiscoveryResult(
                "failed",
                true,
                [listing.Detail ?? $"Provider discovery failed with status {(int)listing.Status}."],
                []);
        }

        // The vendor lists more than the models a chat or an embedding client can call. An image, a speech or a
        // moderation model offered here is a binding an operator can pick and a review then fails on, so they
        // are left out and counted rather than described as something they are not.
        var servable = listing.Models.Where(OpenAiModels.IsServable).ToList();
        var skipped = listing.Models.Count - servable.Count;

        List<string> warnings = [];
        if (servable.Count == 0)
        {
            warnings.Add(NoModelsDiscovered);
        }

        if (skipped > 0)
        {
            warnings.Add(
                $"{skipped} listed model(s) are not offered: they are image, speech, transcription or moderation "
                + "models, which this host binds to no operation.");
        }

        return new ProviderModelDiscoveryResult(
            "succeeded",
            true,
            warnings,
            [.. servable.Select(OpenAiModels.Describe)]);
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var listing = await OpenAiModels.ListAsync(endpoint, ct).ConfigureAwait(false);

        return (int)listing.Status >= 400
            ? DriverFailureMapper.Failed(listing.Status, listing.Detail)
            : DriverFailureMapper.Verified(
                $"Verified OpenAI connectivity for '{endpoint.BaseUrl}'.",
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

        if (Options(endpoint) is not { } options)
        {
            return UnreachableClients.ChatClient();
        }

        var credential = Credential(endpoint);

        if (UsesResponsesApi(protocolMode, model))
        {
            var client = new OpenAIClient(credential, options);
            return client.GetResponsesClient().AsIChatClient(model.RemoteModelId);
        }

        return new ChatClient(model.RemoteModelId, credential, options).AsIChatClient();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Provider-managed sessions, background responses and the preference for the Responses API all follow
    ///     from the protocol mode the client was built on: they are affordances of that surface and the Chat
    ///     Completions surface has no equivalent for any of them.
    /// </remarks>
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        _ = endpoint;
        var usesResponses = UsesResponsesApi(protocolMode, model);

        return new ProviderRuntimeCapabilities(
            usesResponses,
            usesResponses,
            usesResponses,
            usesResponses);
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

        if (Options(endpoint) is not { } options)
        {
            return UnreachableClients.EmbeddingGenerator();
        }

        return new EmbeddingClient(model.RemoteModelId, Credential(endpoint), options).AsIEmbeddingGenerator();
    }

    /// <inheritdoc />
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        return OpenAiFailures.Classify(exception);
    }

    /// <summary>Whether a host belongs to Azure's AI resources.</summary>
    /// <param name="host">The URL host component.</param>
    private static bool IsAzureAiHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // The trailing dot of a fully qualified name is dropped, so a host written that way is the same host.
        var normalized = host.TrimEnd('.');

        return AzureAiHostSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool UsesResponsesApi(string protocolMode, ProviderModelDescriptor model)
    {
        return ProviderVocabulary.ValuesEqual(protocolMode, ResponsesProtocol)
               || (ProviderVocabulary.ValuesEqual(protocolMode, ProviderDeclaredProtocolModes.Auto)
                   && ProviderVocabulary.Names(model.SupportedProtocolModes, ResponsesProtocol));
    }

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
    private static OpenAIClientOptions? Options(ProviderEndpoint endpoint)
    {
        if (endpoint.HostContext?.Http is not { } http)
        {
            return null;
        }

        return new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint.BaseUrl, UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(OpenAiTransport.Client(endpoint, ProviderHttpPurpose.Runtime)),
        };
    }
}
