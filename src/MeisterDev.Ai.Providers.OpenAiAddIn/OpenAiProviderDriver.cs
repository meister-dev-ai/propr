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
        if (DriverFailureMapper.IsFailure(listing.Status))
        {
            return DriverFailureMapper.DiscoveryFailed(listing.Status, listing.Detail);
        }

        // The vendor lists more than the models a review can call. An image, speech, moderation, realtime, audio or
        // search model offered here is a binding an operator can pick and a review then fails on, so these models
        // are left out and counted.
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
                $"{skipped} listed model(s) are not offered: they are image, video, speech, transcription, "
                + "moderation, realtime, audio or search models, which a review cannot call.");
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

        return DriverFailureMapper.IsFailure(listing.Status)
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

        // The adapter is wrapped so that the host hands the client its reasoning request in neutral terms. OpenAI
        // options that the host builds are a foreign type for the copy of the OpenAI library this add-in loads.
        if (UsesResponsesApi(protocolMode, model))
        {
            var client = new OpenAIClient(credential, options);
            return new OpenAiReasoningChatClient(client.GetResponsesClient().AsIChatClient(model.RemoteModelId), usesResponsesApi: true);
        }

        return new OpenAiReasoningChatClient(new ChatClient(model.RemoteModelId, credential, options).AsIChatClient(), usesResponsesApi: false);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Provider-managed sessions, background responses and the preference for the Responses API are reported
    ///     from the decision that selects the API in <see cref="CreateChatClient" />, so they match the client that
    ///     method builds. They are affordances of the Responses API, and Chat Completions has no equivalent for any
    ///     of them.
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
    public bool SupportsLowReasoningEffort(string protocolMode)
    {
        return ProviderVocabulary.ValuesEqual(protocolMode, ProviderDeclaredProtocolModes.Auto)
               || ProviderVocabulary.ValuesEqual(protocolMode, ResponsesProtocol)
               || ProviderVocabulary.ValuesEqual(protocolMode, ChatCompletionsProtocol);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The catalog's levels apply to every model it lists. For the gpt-6 models, which the bundled snapshot
    ///     predates, the family reports the values the Responses API accepts for each of them.
    /// </remarks>
    public IReadOnlyList<string>? DescribeReasoningLevels(string remoteModelId, IReadOnlyList<string>? catalogLevels)
    {
        return catalogLevels ?? OpenAiModels.ReasoningLevelsOf(remoteModelId);
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

    /// <summary>Whether a chat call for this binding goes out on the Responses API.</summary>
    /// <param name="protocolMode">The protocol mode of the binding.</param>
    /// <param name="model">The bound model, with the protocol modes it lists.</param>
    /// <remarks>
    ///     <para>
    ///         An explicit protocol mode is followed as written. In auto mode the call goes out on the Responses API
    ///         unless the model lists this family's Chat Completions mode and not its Responses mode. OpenAI
    ///         recommends the Responses API, and from GPT-5.4 on, its Chat Completions endpoint refuses function
    ///         tools when a reasoning effort other than none applies. Some models, such as GPT-6.1 Terra, accept
    ///         function tools only on the Responses API. A file review sends function tools, so a model registered
    ///         with auto alone is reached on the Responses API.
    ///     </para>
    ///     <para>
    ///         OpenAI serves a few models, such as its audio models, on Chat Completions alone, and discovery leaves
    ///         them out. An operator who enters such a model by hand keeps it on Chat Completions by listing that
    ///         mode without Responses, or by binding it with the Chat Completions mode.
    ///     </para>
    /// </remarks>
    private static bool UsesResponsesApi(string protocolMode, ProviderModelDescriptor model)
    {
        if (ProviderVocabulary.ValuesEqual(protocolMode, ResponsesProtocol))
        {
            return true;
        }

        if (!ProviderVocabulary.ValuesEqual(protocolMode, ProviderDeclaredProtocolModes.Auto))
        {
            return false;
        }

        return ProviderVocabulary.Names(model.SupportedProtocolModes, ResponsesProtocol)
               || !ProviderVocabulary.Names(model.SupportedProtocolModes, ChatCompletionsProtocol);
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
