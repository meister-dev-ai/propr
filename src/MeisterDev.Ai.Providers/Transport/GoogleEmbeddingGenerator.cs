// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeisterDev.Ai.Providers.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Transport;

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

    private readonly HttpClient _httpClient;
    private readonly IGoogleCredentialSource _credentials;
    private readonly ProviderEndpoint _endpoint;
    private readonly ProviderModelDescriptor _model;
    private readonly int _dimensions;

    /// <summary>Initializes a new instance of the <see cref="GoogleEmbeddingGenerator" /> class.</summary>
    /// <param name="httpClient">The egress-guarded client runtime traffic goes through.</param>
    /// <param name="credentials">Authenticates each request for the surface the endpoint is.</param>
    /// <param name="endpoint">Where to reach the provider.</param>
    /// <param name="model">The embedding model this generator is bound to.</param>
    /// <param name="dimensions">The output dimension count to ask for, or zero for the model's own.</param>
    public GoogleEmbeddingGenerator(
        HttpClient httpClient,
        IGoogleCredentialSource credentials,
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        this._httpClient = httpClient;
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
    public void Dispose()
    {
        // The HttpClient is owned by the factory that produced it.
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

        using var response = await this._httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
