// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn;

/// <summary>
///     Generates embeddings through Google's <c>embedContent</c> method.
/// </summary>
/// <remarks>
///     One request per input rather than the batch method, because the batch form differs between the Gemini API
///     and Vertex while the single form does not, and an embedding call is not where this system spends its time.
/// </remarks>
public sealed class GoogleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const string EmbedContentMethod = "embedContent";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IProviderHttpClientFactory? _http;
    private readonly IGoogleCredentialSource _credentials;
    private readonly ProviderEndpoint _endpoint;
    private readonly ProviderModelDescriptor _model;
    private readonly int _dimensions;

    /// <summary>Initializes a new instance of the <see cref="GoogleEmbeddingGenerator" /> class.</summary>
    /// <param name="http">
    ///     The host's client factory, from which the egress-guarded client runtime traffic goes through is taken,
    ///     or null where the host supplied none.
    /// </param>
    /// <param name="credentials">Authenticates each request for the surface the endpoint is.</param>
    /// <param name="endpoint">Where to reach the provider.</param>
    /// <param name="model">The embedding model this generator is bound to.</param>
    /// <param name="dimensions">The output dimension count to ask for, or zero for the model's own.</param>
    /// <remarks>
    ///     The factory is held rather than a client, and the client is taken on the first call, for the reason
    ///     given on the chat client: a host builds one of these to describe a family as well as to call it.
    /// </remarks>
    public GoogleEmbeddingGenerator(
        IProviderHttpClientFactory? http,
        IGoogleCredentialSource credentials,
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        this._http = http;
        this._credentials = credentials;
        this._endpoint = endpoint;
        this._model = model;
        this._dimensions = dimensions;
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var generated = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            generated.Add(await this.EmbedAsync(value, cancellationToken).ConfigureAwait(false));
        }

        return generated;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing to release. The client each call takes from the host's factory is disposed by that call, and
    ///     disposing one stops at the host's pooled handler chain, which the factory keeps and rotates on its own
    ///     schedule.
    /// </remarks>
    public void Dispose()
    {
    }

    private async Task<Embedding<float>> EmbedAsync(string value, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = value } },
            },
        };

        if (this._dimensions > 0)
        {
            payload["outputDimensionality"] = this._dimensions;
        }

        var uri = GoogleEndpointResolution.BuildModelUri(this._endpoint, this._model.RemoteModelId, EmbedContentMethod);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json"),
        };
        await this._credentials.AuthenticateAsync(request, this._endpoint, cancellationToken).ConfigureAwait(false);

        // The same headers the chat path applies. An operator configures them on the connection, and a gateway
        // in front of Google reads them on every request it routes, embeddings included.
        foreach (var header in this._endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Taken per call. The factory hands back a thin client over the host's pooled handler chain, so holding
        // one saves nothing, and a field assigned with ??= is written by every call that finds it empty.
        using var client = GoogleTransport.Client(this._http, ProviderHttpPurpose.Runtime);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google rejected the embedding request: {body}", null, response.StatusCode);
        }

        var values = (JsonNode.Parse(body) as JsonObject)?["embedding"]?["values"] as JsonArray
                     ?? throw new HttpRequestException("Google returned an embedding response with no vector in it.");

        return new Embedding<float>(values.Select(entry => entry?.GetValue<float>() ?? 0f).ToArray())
        {
            ModelId = this._model.RemoteModelId,
        };
    }
}
