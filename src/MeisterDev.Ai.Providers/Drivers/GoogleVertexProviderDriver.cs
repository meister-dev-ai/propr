// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json.Nodes;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Google's Gemini models, on either the Gemini API or Vertex AI in the customer's own project.
/// </summary>
/// <remarks>
///     One driver serves both because they speak the same protocol; only how a request is addressed and
///     authenticated differs, and the endpoint URL says which. Vertex is the reason the driver exists — a
///     customer who requires inference inside their own GCP project cannot be served by a gateway — while the
///     Gemini API is the same protocol without the project boundary.
/// </remarks>
public sealed class GoogleVertexProviderDriver(
    IHttpClientFactory httpClientFactory,
    IGoogleCredentialSource credentials,
    bool allowPrivateEgress,
    bool allowInsecureScheme) : IAiProviderDriver
{
    /// <inheritdoc />
    public AiProviderKind ProviderKind => AiProviderKind.GoogleVertex;

    /// <inheritdoc />
    public IReadOnlyList<AiProtocolMode> SupportedProtocolModes { get; } =
        [AiProtocolMode.Auto, AiProtocolMode.GoogleGenerateContent, AiProtocolMode.Embeddings];

    /// <inheritdoc />
    /// <remarks>
    ///     One driver, two surfaces. Which one a target is, is decided by its host, and on Vertex the location is
    ///     part of that host - so a project pinned to a region is visible in the URL rather than hidden in a
    ///     setting.
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

        if (GoogleEndpointResolution.IsVertex(target.BaseUrl))
        {
            if (GoogleEndpointResolution.LocationFromHost(uri.Host) is null)
            {
                return "A Vertex AI endpoint must name its location, for example "
                       + "https://europe-west4-aiplatform.googleapis.com.";
            }

            // A service-account key is a JSON document, not a key string, and the surface will not take one
            // without the other.
            return target.HasApiKey
                ? null
                : "Vertex AI requires the JSON key of a service account that may call the Vertex AI API.";
        }

        if (!uri.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) && !allowPrivateEgress)
        {
            return "A Google connection must target a Google host, for example "
                   + "https://generativelanguage.googleapis.com.";
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
        if ((int)status >= 400)
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
                await credentials.AuthenticateAsync(probe, endpoint, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException configuration)
            {
                return DriverFailureMapper.Failed(HttpStatusCode.BadRequest, configuration.Message);
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
        if ((int)status >= 400)
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
        AiProtocolMode protocolMode)
    {
        AiProtocolModeSupport.Require(this.ProviderKind, this.SupportedProtocolModes, protocolMode);

        return new GoogleGenerateContentChatClient(
            httpClientFactory.CreateClient("AiProviderRuntime"),
            credentials,
            endpoint,
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

        // Gemini caches a repeated prefix on its own and reports what it served from cache, so caching is
        // claimed even though — unlike Anthropic — there is no breakpoint for a caller to place.
        return ProviderRuntimeCapabilities.None with { SupportsPromptCaching = true };
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions)
    {
        _ = protocolMode;

        return new GoogleEmbeddingGenerator(
            httpClientFactory.CreateClient("AiProviderRuntime"),
            credentials,
            endpoint,
            model,
            dimensions);
    }

    private const string VertexDiscoveryNotice =
        "Vertex AI does not list its models on this endpoint; enter the model IDs to use, for example "
        + "'gemini-3-pro'.";

    private static string Describe(string body, HttpStatusCode status)
    {
        try
        {
            if ((JsonNode.Parse(body) as JsonObject)?["error"]?["message"]?.GetValue<string>() is { } message)
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
            if (entry["name"]?.GetValue<string>() is not { } name)
            {
                continue;
            }

            var methods = (entry["supportedGenerationMethods"] as JsonArray)?
                .Select(method => method?.GetValue<string>())
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

            models.Add(
                new ProviderDiscoveredModel(
                    id,
                    entry["displayName"]?.GetValue<string>() ?? id,
                    generates ? [AiOperationKind.Chat] : [AiOperationKind.Embedding],
                    generates
                        ? [AiProtocolMode.Auto, AiProtocolMode.GoogleGenerateContent]
                        : [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
                    SupportsStructuredOutput: generates,
                    SupportsToolUse: generates));
        }

        return models;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        Uri uri,
        ProviderEndpoint endpoint,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        try
        {
            await credentials.AuthenticateAsync(request, endpoint, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException configuration)
        {
            // Shaped like a provider rejection so one reader handles both: a credential this system refuses to
            // use and a credential the provider refuses to accept are the same problem to an operator.
            var refusal = new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = configuration.Message },
            };

            return (HttpStatusCode.BadRequest, refusal.ToJsonString());
        }

        using var client = httpClientFactory.CreateClient("AiProviderAdmin");
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}
