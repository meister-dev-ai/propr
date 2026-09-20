// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2.Responses;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;
using MeisterDev.Ai.Providers.Usage;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn;

/// <summary>
///     Google's Gemini models, on either the Gemini API or Vertex AI in the customer's own project.
/// </summary>
/// <remarks>
///     <para>
///         One driver serves both because they speak the same protocol; only how a request is addressed and
///         authenticated differs, and the endpoint URL says which. Vertex is the reason the driver exists — a
///         customer who requires inference inside their own GCP project cannot be served by a gateway — while the
///         Gemini API is the same protocol without the project boundary.
///     </para>
///     <para>
///         Constructed by the loader with no arguments, so everything it needs at call time comes from the
///         endpoint it is handed: the network through the host's client factory, and the credential from what the
///         connection stored.
///     </para>
/// </remarks>
public sealed class GoogleVertexProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "meisterdev/googleVertex";

    /// <summary>The protocol mode this family owns: Google's generateContent method.</summary>
    public const string GenerateContentProtocol = FamilyKey + ":GoogleGenerateContent";

    /// <summary>The authentication mode the Gemini API reads from a header.</summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    /// <summary>The authentication mode Vertex mints a token from.</summary>
    public const string GcpAdcAuth = FamilyKey + ":GcpAdc";

    /// <inheritdoc />
    /// <summary>
    ///     What this family is, stated once. It declares no configuration fields and no actions: which of the two
    ///     Google surfaces a connection reaches follows from its base URL, which is a host column.
    /// </summary>
    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,

        // Both surfaces this family reaches are in the label, because the Gemini API is configured here too and
        // an operator looking for it would otherwise not find a family that names only Vertex.
        Label = "Google Gemini / Vertex AI",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
            ["GoogleVertex"],
            [ApiKeyAuth, GcpAdcAuth],
            [GenerateContentProtocol]),

        // Two surfaces, two authentication modes. The Gemini API takes a key in x-goog-api-key, which is the plain
        // API-key mode. Vertex takes a Google credential document — a service-account key or a workload-identity
        // configuration — that a token is minted from. The two take different material, which is why the mode has
        // to match the surface: a key string is not something a token can be minted from, and a credential
        // document is not something the Gemini API reads.
        AuthModes =
        [
            new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey]) { Label = "API Key" },
            new ProviderDeclaredAuthMode(
                GcpAdcAuth,
                [
                    new ProviderCredentialField(
                        GoogleCredentialSource.ServiceAccountField,
                        "Service account key (JSON)",
                        Hint: "The whole JSON document of a service account that may call the Vertex AI API, or a "
                              + "workload-identity configuration."),
                ])
            {
                // Written out rather than left as the initials, because an operator matches this against the
                // name Google documents the mechanism under.
                Label = "Google Application Default Credentials",
            },
        ],
        ProtocolModes = new ProviderDeclaredProtocolModes(
        [
            ProviderDeclaredProtocolModes.Auto,
            GenerateContentProtocol,
            ProviderDeclaredProtocolModes.Embeddings,
        ])
        {
            // The method name as Google spells it on the wire, which is how an operator finds it in Google's own
            // documentation.
            Labels = new Dictionary<string, string>
            {
                [GenerateContentProtocol] = "Google generateContent",
            },
        },
        ConnectionForm = new ProviderConnectionForm(
            NamePlaceholder: "Gemini on Vertex (europe-west4)",
            BaseUrlPlaceholder: "https://europe-west4-aiplatform.googleapis.com",

            // The two surfaces are reached at different addresses, and the Vertex one carries the location, so
            // the address is what decides which of them a connection talks to.
            BaseUrlHint: "A Vertex host names the location it serves, or names none for the global endpoint, "
                         + "which is where the newest models are served. For the Gemini API use "
                         + "https://generativelanguage.googleapis.com instead.",

            // Vertex addresses a model under a project, and no request can be built without one.
            RequiredQueryParam: "project",
            QueryParamPlaceholder: "project=your-gcp-project"),

        // Both surfaces are Google-hosted, and on Vertex the location is part of the hostname, which is why this
        // is a suffix and not a host.
        ReachedHostPatterns = [".googleapis.com"],
        ConformanceInputs = new ProviderConformanceInputs(
            GcpAdcAuth,

            // Google reports its prompt count inclusive of the cached portion and its thinking tokens outside the
            // candidate count, so the output total here is exclusive of the 200 reasoning tokens beside it.
            "{\"inputTokenCount\":4120,\"outputTokenCount\":207,\"totalTokenCount\":4527,"
            + "\"cachedInputTokenCount\":4000,\"reasoningTokenCount\":200}"),
    };

    /// <summary>
    ///     Authenticates every request this family sends, holding the minted tokens between calls.
    /// </summary>
    /// <remarks>
    ///     One instance for the family rather than one per connection, because what it caches is keyed by the
    ///     credential it was given and a driver serves every connection of its family from one instance.
    /// </remarks>
    private readonly IGoogleCredentialSource _credentials;

    /// <summary>Initializes a new instance of the <see cref="GoogleVertexProviderDriver" /> class.</summary>
    /// <remarks>The constructor the loader calls. Nothing a family needs at runtime is supplied here.</remarks>
    public GoogleVertexProviderDriver()
        : this(new GoogleCredentialSource())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GoogleVertexProviderDriver" /> class.</summary>
    /// <param name="credentials">Authenticates each request for the surface an endpoint is.</param>
    /// <remarks>
    ///     Takes the credential source so the two surfaces can be exercised without a Google account: minting a
    ///     Vertex token signs a JWT and exchanges it, which is the one thing about this family that cannot be
    ///     driven from a fake endpoint.
    /// </remarks>
    public GoogleVertexProviderDriver(IGoogleCredentialSource credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        this._credentials = credentials;
    }

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
    ///     <para>
    ///         One driver, two surfaces. Which one a target is, is decided by its host, and on Vertex the location
    ///         is part of that host - so a project pinned to a region is visible in the URL rather than hidden in a
    ///         setting.
    ///     </para>
    ///     <para>
    ///         The address is checked before the authentication mode. An endpoint is operator-supplied text, so a
    ///         family that accepted any of it would turn its own probe into a way to reach whatever the host can
    ///         reach. Both Google surfaces are under the host pattern this family declares, which refuses plain
    ///         http, a loopback or private literal and a metadata address in one check; the host's connect-time
    ///         egress guard sits behind it and cannot be opted out of.
    ///     </para>
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
        {
            return "baseUrl must be an absolute URL.";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "baseUrl must use https.";
        }

        if (GoogleEndpointResolution.IsVertex(target.BaseUrl))
        {
            // The surface decides which of the two credentials is usable, so a mode naming the other one is
            // refused when the profile is saved. On Vertex the API-key mode would hand a key string to a token
            // exchange that cannot read one.
            if (ProviderVocabulary.ValuesEqual(target.AuthMode, ApiKeyAuth))
            {
                return $"A Vertex AI endpoint mints a token from a Google credential document. Set this "
                       + $"connection's authentication to '{Named(GcpAdcAuth)}', or point it at a Gemini API "
                       + $"endpoint to keep using '{Named(target.AuthMode)}'.";
            }

            // Any other mode is refused too. Naming only the swapped one left every mode this family does not
            // declare accepted whenever a secret was present, and the secret then travelled as whatever the
            // surface expects.
            // Named as it was sent, and with the family, because a mode this family does not declare reaches
            // here too and neither is obvious from a provider's own authentication failure.
            if (!ProviderVocabulary.ValuesEqual(target.AuthMode, GcpAdcAuth))
            {
                return $"The '{FamilyKey}' family does not read '{target.AuthMode}' authentication on a Vertex AI "
                       + $"endpoint. It takes '{Named(GcpAdcAuth)}'.";
            }

            // A service-account key is a JSON document, not a key string, and the surface will not take one
            // without the other.
            return target.HasApiKey
                ? null
                : "Vertex AI requires the JSON key of a service account that may call the Vertex AI API.";
        }

        if (!ReachesHost(uri.Host))
        {
            return "A Google connection must target a Google host, for example "
                   + "https://generativelanguage.googleapis.com.";
        }

        // The other half of the same rule, and the one that matters most: the Gemini API reads its credential
        // from a header, so a profile that stored a credential document here would send a private key in one.
        if (ProviderVocabulary.ValuesEqual(target.AuthMode, GcpAdcAuth))
        {
            return $"The Gemini API reads an API key from a header. Set this connection's authentication to "
                   + $"'{Named(ApiKeyAuth)}', or point it at a Vertex AI endpoint to keep using "
                   + $"'{Named(target.AuthMode)}'.";
        }

        if (!ProviderVocabulary.ValuesEqual(target.AuthMode, ApiKeyAuth))
        {
            return $"The '{FamilyKey}' family does not read '{target.AuthMode}' authentication on the Gemini API. "
                   + $"It takes '{Named(ApiKeyAuth)}'.";
        }

        return target.HasApiKey ? null : "An API key is required for the Gemini API.";
    }

    /// <inheritdoc />
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (GoogleEndpointResolution.BuildModelsUri(endpoint) is not { } uri)
        {
            return new ProviderModelDiscoveryResult("succeeded", true, [VertexDiscoveryNotice], []);
        }

        var (status, body) = await this.SendAsync(uri, endpoint, ct).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            return new ProviderModelDiscoveryResult("failed", true, [Describe(body, status)], []);
        }

        var models = ReadModels(body);
        return new ProviderModelDiscoveryResult(
            "succeeded",
            true,
            models.Count == 0 ? ["No models were discovered from the provider. Manual model entry remains available."] : [],
            models);
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (GoogleEndpointResolution.BuildModelsUri(endpoint) is not { } uri)
        {
            // Vertex publishes no model list on this surface, so the credential is exercised by minting a token
            // for it — which is the half of the configuration most likely to be wrong.
            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, endpoint.BaseUrl);
                await this._credentials.AuthenticateAsync(probe, endpoint, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (IsAuthenticationFailure(failure))
            {
                return DriverFailureMapper.Failed(AuthenticationFailureStatus(failure), failure.Message);
            }

            return GoogleEndpointResolution.ResolveProject(endpoint) is { } project
                ? DriverFailureMapper.Verified(
                    $"Accepted the Vertex AI credential for project '{project}'.",
                    [VertexDiscoveryNotice])
                : DriverFailureMapper.Failed(
                    HttpStatusCode.BadRequest,
                    "A Vertex AI connection must name its GCP project as a 'project' query parameter.");
        }

        var (status, body) = await this.SendAsync(uri, endpoint, ct).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            return DriverFailureMapper.Failed(status, Describe(body, status));
        }

        var models = ReadModels(body);
        return DriverFailureMapper.Verified(
            $"Verified Google connectivity for '{endpoint.BaseUrl}' ({models.Count} models).",
            models.Count == 0 ? ["No models were discovered from the provider. Manual model entry remains available."] : []);
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        AiProtocolModeSupport.Require(Declared.Key, this.SupportedProtocolModes, protocolMode);

        return new GoogleGenerateContentChatClient(endpoint.HostContext?.Http, this._credentials, endpoint, model);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Google reports its prompt count inclusive of the cached portion, so nothing is added back on the input
    ///     side, and its thinking tokens outside the candidate count, so those are added into the output total the
    ///     host bills. Gemini charges no separate cache-write, so that bucket stays zero.
    /// </remarks>
    public ProviderTokenUsage ReadUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return ProviderTokenUsage.Missing;
        }

        var reasoning = usage.ReasoningTokenCount ?? 0;

        return new ProviderTokenUsage(
            usage.InputTokenCount ?? 0,
            (usage.OutputTokenCount ?? 0) + reasoning,
            usage.CachedInputTokenCount ?? 0,
            ReasoningTokens: reasoning);
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

        // Gemini caches a repeated prefix on its own and reports what it served from cache, so caching is
        // claimed even though — unlike Anthropic — there is no breakpoint for a caller to place.
        return ProviderRuntimeCapabilities.None with { SupportsPromptCaching = true };
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _ = protocolMode;

        return new GoogleEmbeddingGenerator(
            endpoint.HostContext?.Http,
            this._credentials,
            endpoint,
            model,
            dimensions);
    }

    private const string VertexDiscoveryNotice =
        "Vertex AI does not list its models on this endpoint; enter the model IDs to use, for example "
        + "'gemini-3-pro'.";

    // Only 2xx is an answer. A redirect carries no model list, and reading one as a body produced an empty
    // listing reported as a success.
    private static bool IsSuccess(HttpStatusCode status)
    {
        return (int)status is >= 200 and <= 299;
    }

    private static string Describe(string body, HttpStatusCode status)
    {
        try
        {
            // An endpoint in front of Vertex states its error as a string where Vertex states an object, so the
            // step into `message` is taken through the type rather than by name.
            var error = GoogleJson.Field(JsonNode.Parse(body), "error");
            if (GoogleJson.AsText(GoogleJson.Field(error, "message") ?? error) is { } message)
            {
                return message;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A body that is not JSON is reported as the status alone rather than pasted back at the operator.
        }

        return $"Provider request failed with status {(int)status}.";
    }

    private static IReadOnlyList<ProviderDiscoveredModel> ReadModels(string body)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(body) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        var models = new List<ProviderDiscoveredModel>();
        foreach (var entry in (payload?["models"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (GoogleJson.AsText(entry["name"]) is not { } name)
            {
                continue;
            }

            var methods = (entry["supportedGenerationMethods"] as JsonArray)?
                .Select(GoogleJson.AsText)
                .OfType<string>()
                .ToList() ?? [];

            var generates = methods.Contains("generateContent", StringComparer.OrdinalIgnoreCase);
            var embeds = methods.Contains("embedContent", StringComparer.OrdinalIgnoreCase);
            if (!generates && !embeds)
            {
                // A model that neither answers nor embeds — an image or media model — has no use here, and
                // offering one only produces a call that cannot work.
                continue;
            }

            var id = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name["models/".Length..] : name;

            // A model advertising both methods can do both, and each one carries its own protocol mode, so both are
            // kept. Dropping either would leave the model unbindable for that purpose, since a purpose is bound
            // against the operation kinds and the protocol modes discovery recorded.
            List<AiOperationKind> operations = [];
            List<string> protocolModes = [ProviderDeclaredProtocolModes.Auto];

            if (generates)
            {
                operations.Add(AiOperationKind.Chat);
                protocolModes.Add(GenerateContentProtocol);
            }

            if (embeds)
            {
                operations.Add(AiOperationKind.Embedding);
                protocolModes.Add(ProviderDeclaredProtocolModes.Embeddings);
            }

            models.Add(
                new ProviderDiscoveredModel(
                    id,
                    GoogleJson.AsText(entry["displayName"]) ?? id,
                    operations,
                    protocolModes,
                    SupportsStructuredOutput: generates,
                    SupportsToolUse: generates));
        }

        return models;
    }

    /// <summary>
    ///     The label an operator sees where an authentication mode is offered, so a refusal names the mode the way
    ///     the form does and not by the value it persists as.
    /// </summary>
    /// <param name="mode">The authentication mode to name.</param>
    private static string Named(string mode)
    {
        var declared = Declared.AuthModes
            .FirstOrDefault(candidate => ProviderVocabulary.ValuesEqual(candidate.Mode, mode));

        return declared?.Label ?? mode;
    }

    /// <summary>Whether a host is one of the patterns this family declared it reaches.</summary>
    /// <param name="host">The host component of the configured URL.</param>
    private static bool ReachesHost(string host)
    {
        var normalized = host.TrimEnd('.');

        return Declared.ReachedHostPatterns.Any(pattern => pattern.StartsWith('.')
            ? normalized.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        Uri uri,
        ProviderEndpoint endpoint,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        try
        {
            await this._credentials.AuthenticateAsync(request, endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsAuthenticationFailure(failure))
        {
            // A credential this system refuses to use and a credential the provider refuses to accept are the
            // same problem to an operator, so both leave here in the shape one reader handles.
            return Refusal(AuthenticationFailureStatus(failure), failure.Message);
        }

        try
        {
            using var client = GoogleTransport.Client(endpoint, ProviderHttpPurpose.Admin);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            // Read against the bound the use needs. A refused call's body is kept only to say what the endpoint
            // answered; a model list is parsed, so it is read whole or the read fails.
            var bound = response.IsSuccessStatusCode
                ? ProviderResponseBody.MaximumDocumentBytes
                : ProviderResponseBody.MaximumDetailBytes;

            var (body, truncated) = await ProviderResponseBody
                .ReadBoundedAsync(response.Content, bound, ct)
                .ConfigureAwait(false);

            // A model list the host did not read to the end cannot be parsed, and reporting the models found in
            // the part that was read would present an incomplete list as the endpoint's own.
            return truncated && response.IsSuccessStatusCode
                ? Refusal(
                    HttpStatusCode.BadGateway,
                    $"The provider's model list is longer than the {ProviderResponseBody.MaximumDocumentBytes} "
                    + "bytes this host reads from a response body.")
                : (response.StatusCode, body);
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or IOException)
        {
            // An endpoint that could not be reached is an ordinary misconfiguration, and both callers of this
            // method report what it returns. Letting the transport's exception past them would turn a wrong base
            // URL into an internal error naming nothing an operator can act on.
            return Refusal(HttpStatusCode.ServiceUnavailable, unreachable.Message);
        }
    }

    /// <summary>
    ///     Reports whether a failure raised while authenticating a request describes the credential or the
    ///     connection, and is therefore reported to the operator. Minting a Vertex token is a call to Google's
    ///     token endpoint, so it fails the ways any other call fails: the endpoint refuses the credential, or it
    ///     cannot be reached. Cancellation is not listed, so it keeps propagating.
    /// </summary>
    /// <param name="failure">The exception the authentication attempt raised.</param>
    /// <returns><see langword="true"/> when the failure is reported instead of propagated.</returns>
    /// <inheritdoc />
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        return GoogleFailures.Classify(exception);
    }

    private static bool IsAuthenticationFailure(Exception failure)
    {
        return failure is InvalidOperationException or TokenResponseException or HttpRequestException or IOException;
    }

    /// <summary>
    ///     The status an authentication failure is reported under. A credential this host refuses and a credential
    ///     the token endpoint refuses are both wrong configuration. A token endpoint that could not be reached is
    ///     not, and gets the same status the model-list call uses for an unreachable host.
    /// </summary>
    /// <param name="failure">The exception the authentication attempt raised.</param>
    /// <returns>The status to report the failure under.</returns>
    private static HttpStatusCode AuthenticationFailureStatus(Exception failure)
    {
        return failure is HttpRequestException or IOException
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.BadRequest;
    }

    /// <summary>
    ///     A refusal shaped the way Google shapes one, so a failure this driver raised and a failure the provider
    ///     answered with are read by the same code.
    /// </summary>
    /// <param name="status">The status to report it under.</param>
    /// <param name="message">What to tell the operator.</param>
    private static (HttpStatusCode Status, string Body) Refusal(HttpStatusCode status, string message)
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = message },
        };

        return (status, body.ToJsonString());
    }
}
