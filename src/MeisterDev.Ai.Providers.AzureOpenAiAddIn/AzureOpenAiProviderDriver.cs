// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using Azure.Identity;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using MeisterDev.Ai.Providers.Resilience;

namespace MeisterDev.Ai.Providers.AzureOpenAiAddIn;

/// <summary>
///     Azure OpenAI and Azure AI Foundry, reached at the resource's OpenAI-compatible surface.
/// </summary>
/// <remarks>
///     <para>
///         The resource is reached at its <c>/openai/v1/</c> surface with the OpenAI client library. That surface
///         takes the deployment name as the model of the request instead of as a path segment, and needs no
///         api-version query parameter. Building on that library is also what lets the family send on the client
///         the host supplies, which the Azure-specific library's own transport could not be given.
///     </para>
///     <para>
///         The host this family may be pointed at is pinned to Microsoft's own AI hostnames. That is a control
///         over the credential rather than over the address: a profile configured for this family authenticates
///         with a resource key or a Microsoft Entra token, and one pointed elsewhere would send that credential
///         to a host Microsoft does not control. The hostnames are declared as the hosts this family reaches, so
///         an operator and a tenant's endpoint restriction read the same list the family enforces.
///     </para>
/// </remarks>
public sealed class AzureOpenAiProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "meisterdev/azureOpenAi";

    /// <summary>This family's OpenAI Responses API shape, as an Azure resource serves it.</summary>
    public const string ResponsesProtocol = FamilyKey + ":Responses";

    /// <summary>This family's chat completions shape.</summary>
    public const string ChatCompletionsProtocol = FamilyKey + ":ChatCompletions";

    /// <summary>The authentication mode that sends a resource key in the header the resource reads.</summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    /// <summary>The authentication mode that mints a token from the host's Azure credential chain.</summary>
    public const string AzureIdentityAuth = FamilyKey + ":AzureIdentity";

    /// <summary>
    ///     Header an Azure OpenAI resource reads a resource key from. The v1 surface also accepts the key as a
    ///     bearer token, but this header is the one specific to the resource, so a key and an Entra token stay
    ///     distinguishable on the wire.
    /// </summary>
    private const string AzureApiKeyHeaderName = "api-key";

    /// <summary>
    ///     Scope a Microsoft Entra token for an Azure OpenAI resource is requested for. This is the scope the
    ///     resource's own REST security definition declares.
    /// </summary>
    private const string ResourceTokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    ///     Path the OpenAI-compatible surface is served under, relative to the resource root. The trailing
    ///     slash is kept because the client library appends the operation to this address.
    /// </summary>
    private const string V1SurfacePath = "openai/v1/";

    /// <summary>Note attached when the resource lists no models, which leaves manual entry as the way in.</summary>
    private const string NoModelsWarning =
        "No models were discovered from the provider. Manual model entry remains available.";

    /// <summary>
    ///     The hostname suffixes an Azure OpenAI or Azure AI Foundry resource is reached at, private endpoints
    ///     included.
    /// </summary>
    /// <remarks>
    ///     Suffixes rather than hosts because an Azure endpoint is the operator's own resource name followed by
    ///     one of these, and no family can enumerate a tenant's resource names. Stated here and declared from
    ///     here, so the list an operator reads in the inventory is the list this family enforces.
    /// </remarks>
    private static readonly string[] AzureAiHostSuffixes =
    [
        ".openai.azure.com",
        ".services.ai.azure.com",
        ".cognitiveservices.azure.com",
    ];

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

    private readonly Lazy<TokenCredential> _managedIdentity;

    /// <summary>Builds the family as a host loading it from a directory does, with no arguments.</summary>
    public AzureOpenAiProviderDriver()
        : this(null)
    {
    }

    /// <summary>Builds the family over a supplied Azure credential.</summary>
    /// <param name="managedIdentityCredential">
    ///     Credential the managed-identity mode mints tokens from. Defaults to the ambient Azure credential
    ///     chain, the credential a deployed host authenticates with; a caller supplies one to reach a resource
    ///     with a credential it resolved itself.
    /// </param>
    /// <remarks>
    ///     Resolved on first use so a host that configures no managed-identity connection never builds a
    ///     credential chain, and reused after that so the token cache behind it survives between calls.
    /// </remarks>
    public AzureOpenAiProviderDriver(TokenCredential? managedIdentityCredential)
    {
        this._managedIdentity = new Lazy<TokenCredential>(() => managedIdentityCredential ?? new DefaultAzureCredential());
    }

    /// <summary>
    ///     What this family is, stated once. It declares no configuration fields and no actions: a connection's
    ///     resource URL, credential and verification state are host columns.
    /// </summary>
    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,

        // Both product names, because an Azure AI Foundry OpenAI endpoint is configured here and an operator
        // looking for the Foundry name would otherwise pick the non-Azure family.
        Label = "Azure OpenAI / AI Foundry",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
            ["AzureOpenAi"],
            [ApiKeyAuth, AzureIdentityAuth],
            [ProviderDeclaredProtocolModes.Auto, ResponsesProtocol, ChatCompletionsProtocol, ProviderDeclaredProtocolModes.Embeddings]),

        // A resource key goes in the api-key header; the identity mode mints a Microsoft Entra token from the
        // host's credential chain instead, which is how a deployed host reaches a resource with no key stored
        // at all, so that mode declares no field for an operator to enter.
        AuthModes =
        [
            new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey]) { Label = "API Key" },
            new ProviderDeclaredAuthMode(AzureIdentityAuth, AiCredentialFieldSupport.None)
            {
                Label = "Azure Identity",
            },
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
            NamePlaceholder: "Azure OpenAI (prod)",
            BaseUrlPlaceholder: "https://your-resource.openai.azure.com/",

            // A deployment URL is what an operator finds in the portal beside a model, and it reaches this
            // family as an endpoint the client library then appends its own path to.
            BaseUrlHint: "The Azure AI resource endpoint, not a deployment URL.",

            // The api-version parameter is this family's: an Azure resource pins the wire contract to a dated
            // version, which an operator sets here when the default does not match their deployment.
            QueryParamPlaceholder: "api-version=2024-10-21"),

        // An Azure OpenAI or Azure AI Foundry resource always carries one of these hostnames, private
        // endpoints included, and the resource name in front of the suffix is the operator's, which is why
        // these are suffixes and not hosts.
        ReachedHostPatterns = AzureAiHostSuffixes,
        RequestShapeDefaults = new ProviderRequestShapeDefaults(
            RefusedParameterNames: new Dictionary<string, ProviderRequestShapeConstraint>(StringComparer.Ordinal)
            {
                // A reasoning deployment on this protocol rejects the whole request when it carries a sampling
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
    ///     The host is pinned to Azure's own AI hosts because this family serves an Azure OpenAI or Azure AI
    ///     Foundry resource and nothing else: those resources always carry one of these hostnames, private
    ///     endpoints included, and an endpoint elsewhere would be sent an Azure credential by a service that
    ///     cannot read it.
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } urlError)
        {
            return urlError;
        }

        if (!IsAzureAiHost(uri.Host))
        {
            return "Azure OpenAI connections must target an Azure AI host (*.openai.azure.com, "
                   + "*.services.ai.azure.com, or *.cognitiveservices.azure.com).";
        }

        // An Azure AI host is public and always served over https, so neither relaxation the installation may
        // have stated applies here.
        if (ProbeTargetChecks.Egress(uri, allowPrivateEgress: false, allowInsecureScheme: false) is { } egressError)
        {
            return egressError;
        }

        // Azure can authenticate with a managed identity, in which case there is no key to require.
        return ProviderVocabulary.ValuesEqual(target.AuthMode, AzureIdentityAuth)
            ? null
            : ProbeTargetChecks.RequireApiKey(
                target,
                [ApiKeyAuth],
                "An API key or Azure identity is required for this provider.");
    }

    /// <inheritdoc />
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        try
        {
            var models = await this.ListModelsAsync(endpoint, ProviderHttpPurpose.Admin, ct).ConfigureAwait(false);
            var warnings = models.Count == 0 ? [NoModelsWarning] : Array.Empty<string>();

            return new ProviderModelDiscoveryResult("succeeded", true, warnings, models);
        }
        catch (ClientResultException exception)
        {
            return new ProviderModelDiscoveryResult(
                "failed",
                true,
                [AzureOpenAiFailures.Failed(exception).Summary ?? exception.Message],
                []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProviderModelDiscoveryResult(
                "failed",
                true,
                [DriverFailureMapper.Failed(exception).Summary ?? exception.Message],
                []);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The model list is requested here rather than through <see cref="DiscoverModelsAsync" />, because that
    ///     method reports a refused call as a discovery result and never rethrows. Verification decides whether a
    ///     connection may be used, so a refusal has to reach the failure mapping instead of being reduced to a
    ///     warning on a verified result.
    /// </remarks>
    public async Task<ProviderVerificationResult> VerifyAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        try
        {
            var models = await this.ListModelsAsync(endpoint, ProviderHttpPurpose.Admin, ct).ConfigureAwait(false);
            return DriverFailureMapper.Verified(
                $"Verified Azure OpenAI connectivity for '{endpoint.BaseUrl}'.",
                models.Count == 0 ? [NoModelsWarning] : []);
        }
        catch (ClientResultException exception)
        {
            return AzureOpenAiFailures.Failed(exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DriverFailureMapper.Failed(exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The deployment name is carried as the model of the request, which is how the v1 surface addresses a
    ///     deployment.
    /// </remarks>
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        AiProtocolModeSupport.Require(Declared.Key, this.SupportedProtocolModes, protocolMode);

        if (this.Options(endpoint, ProviderHttpPurpose.Runtime) is not { } options)
        {
            return UnreachableClients.ChatClient("Azure OpenAI");
        }

        var authentication = this.Authentication(endpoint);

        if (ProviderVocabulary.ValuesEqual(protocolMode, ChatCompletionsProtocol))
        {
            return new ChatClient(model.RemoteModelId, authentication, options).AsIChatClient();
        }

        return new OpenAIClient(authentication, options).GetResponsesClient().AsIChatClient(model.RemoteModelId);
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        _ = endpoint;
        _ = model;

        var usesResponses = !ProviderVocabulary.ValuesEqual(protocolMode, ChatCompletionsProtocol);
        return new ProviderRuntimeCapabilities(
            usesResponses,
            usesResponses,
            usesResponses,
            usesResponses,
            true,
            true);
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

        if (this.Options(endpoint, ProviderHttpPurpose.Runtime) is not { } options)
        {
            return UnreachableClients.EmbeddingGenerator("Azure OpenAI");
        }

        return new EmbeddingClient(model.RemoteModelId, this.Authentication(endpoint), options).AsIEmbeddingGenerator();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The client library reports a refused call as a <c>ClientResultException</c>, which carries the status
    ///     and the response body the shared rule needs and which the contract assembly cannot name because it
    ///     takes no vendor dependency. Everything else is left to the shared rule.
    /// </remarks>
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        return AzureOpenAiFailures.ClassifyRuntimeFailure(exception);
    }

    /// <summary>Whether a host is one of the Azure AI hostnames this family is pinned to.</summary>
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

    /// <summary>
    ///     Reads the resource's model list off the OpenAI-compatible surface, letting a refusal propagate so each
    ///     caller decides what a refused call means for it.
    /// </summary>
    /// <param name="endpoint">The endpoint carrying the resource URL, credential and its mode.</param>
    /// <param name="purpose">What the call is for.</param>
    /// <param name="ct">Token cancelling the call.</param>
    private async Task<IReadOnlyList<ProviderDiscoveredModel>> ListModelsAsync(
        ProviderEndpoint endpoint,
        ProviderHttpPurpose purpose,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var options = this.Options(endpoint, purpose)
                      ?? throw new InvalidOperationException(AzureOpenAiTransport.NoClientFactory);

        var client = new OpenAIClient(this.Authentication(endpoint), options);
        var response = await client.GetOpenAIModelClient().GetModelsAsync(ct).ConfigureAwait(false);

        return response.Value
            .Select(model => GuessModelCapabilities(model.Id))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    ///     Builds the policy that authenticates each request. A stored key goes in the header the resource reads
    ///     it from; the managed-identity mode mints a token from the Azure credential chain per request, so an
    ///     expiring credential is refreshed without the endpoint holding one.
    /// </summary>
    /// <param name="endpoint">The endpoint carrying the credential and its mode.</param>
    private AuthenticationPolicy Authentication(ProviderEndpoint endpoint)
    {
        return ProviderVocabulary.ValuesEqual(endpoint.AuthMode, AzureIdentityAuth)
            ? new BearerTokenPolicy(this._managedIdentity.Value, ResourceTokenScope)
            : ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(
                new ApiKeyCredential(endpoint.Secret ?? string.Empty),
                AzureApiKeyHeaderName);
    }

    /// <summary>
    ///     The client library's options pointed at the v1 surface of the configured resource and at the host's
    ///     transport, or null where the host supplied no transport.
    /// </summary>
    /// <param name="endpoint">The endpoint the client will address.</param>
    /// <param name="purpose">What the calls on this client are for.</param>
    /// <remarks>
    ///     The transport is taken here rather than on the first call because the client library wants one when it
    ///     is constructed. Null rather than a throw, so describing this family still works where calling it
    ///     cannot: the caller hands back a client that refuses instead.
    /// </remarks>
    private OpenAIClientOptions? Options(ProviderEndpoint endpoint, ProviderHttpPurpose purpose)
    {
        if (endpoint.HostContext?.Http is not { } http)
        {
            return null;
        }

        return new OpenAIClientOptions
        {
            Endpoint = V1Surface(endpoint.BaseUrl),
            Transport = new HttpClientPipelineTransport(AzureOpenAiTransport.Client(endpoint, purpose)),

            // A reasoning deployment can take minutes to answer, which the client library's own default would
            // cut short. Each call stays bounded by the cancellation token it is given.
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };
    }

    /// <summary>
    ///     Resolves the OpenAI-compatible surface of the configured resource. An Azure AI Foundry portal URL
    ///     carries a project path that is not part of the API surface, so only the resource root is kept.
    /// </summary>
    /// <param name="endpointUrl">The configured base URL.</param>
    private static Uri V1Surface(string endpointUrl)
    {
        var uri = new Uri(endpointUrl, UriKind.Absolute);
        return new Uri($"{uri.Scheme}://{uri.Authority}/{V1SurfacePath}");
    }

    internal static ProviderDiscoveredModel GuessModelCapabilities(string remoteModelId)
    {
        var normalized = remoteModelId.Trim();
        var isEmbedding = normalized.Contains("embedding", StringComparison.OrdinalIgnoreCase);

        if (isEmbedding)
        {
            return new ProviderDiscoveredModel(
                normalized,
                normalized,
                [AiOperationKind.Embedding],
                [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
                "cl100k_base",
                MaxInputTokens: 8192,
                MaxContextTokens: null,

                // No width is stated. This surface lists identifiers and nothing else, and an identifier does
                // not say how long a vector the model returns: text-embedding-3-large returns 3072 where
                // text-embedding-3-small returns 1536, and answering one for the other configures a connection
                // whose vectors are the wrong size. The operator enters it, which the form asks for.
                EmbeddingDimensions: null);
        }

        return new ProviderDiscoveredModel(
            normalized,
            normalized,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, ResponsesProtocol, ChatCompletionsProtocol],
            SupportsStructuredOutput: true,
            SupportsToolUse: true);
    }
}
