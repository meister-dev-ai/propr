// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.OpenAiAddIn;

/// <summary>
///     Reads the model list the vendor publishes at <c>/models</c> as <c>{ "data": [ { "id": … } ] }</c>.
/// </summary>
/// <remarks>
///     The list is the only administrative call this family makes, and it doubles as the verification call: an
///     endpoint that answers it with the configured credential is an endpoint a review can use.
/// </remarks>
internal static class OpenAiModels
{
    /// <summary>What an endpoint answered when asked for its models.</summary>
    /// <param name="Status">The status the endpoint replied with.</param>
    /// <param name="Models">The model identifiers it listed, empty when it listed none or refused.</param>
    /// <param name="Detail">What it said when it refused, or null when it did not.</param>
    internal readonly record struct Listing(HttpStatusCode Status, IReadOnlyList<string> Models, string? Detail);

    /// <summary>Asks an endpoint for its models.</summary>
    /// <param name="endpoint">Where to reach the provider, and what to authenticate with.</param>
    /// <param name="ct">Cancels the call.</param>
    internal static async Task<Listing> ListAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var request = Request(endpoint);
        using var client = OpenAiTransport.Client(endpoint, ProviderHttpPurpose.Admin);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        // Read against the bound the use needs. A refusal is kept only to tell an operator what the endpoint
        // said, so the opening of it is enough and the rest is dropped rather than carried into a summary.
        if (!response.IsSuccessStatusCode)
        {
            var (detail, _) = await ProviderResponseBody
                .ReadBoundedAsync(response.Content, ProviderResponseBody.MaximumDetailBytes, ct)
                .ConfigureAwait(false);

            return new Listing(
                response.StatusCode,
                [],
                string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail);
        }

        var (payload, truncated) = await ProviderResponseBody
            .ReadBoundedAsync(response.Content, ProviderResponseBody.MaximumDocumentBytes, ct)
            .ConfigureAwait(false);

        // A model list the host did not read to the end cannot be parsed, and reporting the models found in the
        // part that was read would present an incomplete list as the endpoint's own. Reported as a refusal
        // rather than raised, because both callers of this method read a refusal and neither catches an
        // exception: raising one would reach the operator as an internal error naming nothing to act on.
        if (truncated)
        {
            return new Listing(
                HttpStatusCode.BadGateway,
                [],
                $"The provider's model list is longer than the {ProviderResponseBody.MaximumDocumentBytes} bytes "
                + "this host reads from a response body.");
        }

        if (!ProviderModelListJson.TryReadModelIds(payload, out var models))
        {
            return new Listing(
                HttpStatusCode.BadGateway,
                [],
                "The provider answered with a body this host could not read as a model list.");
        }

        return new Listing(response.StatusCode, models, null);
    }

    /// <summary>
    ///     What a model identifier says about the model behind it, for an endpoint that publishes identifiers and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     The list carries an id and nothing else, so the operation kinds and the protocol modes are inferred from
    ///     the id. An embedding model is named as one; everything else is offered for chat on both protocol modes the
    ///     vendor serves, and a model that turns out not to serve one says so on its first call.
    /// </remarks>
    /// <param name="remoteModelId">The identifier the endpoint listed.</param>
    // The vector width each documented embedding model returns. The listing carries an id and nothing else, so
    // a model absent from this table reports no width instead of a guessed one: text-embedding-3-large returns
    // 3072, and answering 1536 for it configured a connection that produces vectors of the wrong size.
    private static readonly FrozenDictionary<string, int> EmbeddingDimensionsByModel =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["text-embedding-3-small"] = 1536,
            ["text-embedding-3-large"] = 3072,
            ["text-embedding-ada-002"] = 1536,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // The families the vendor lists that neither this host nor this family serves: images, speech, transcription
    // and moderation. The listing carries an id and nothing else, so they are told apart by the id, which is how
    // the embedding models are told apart too.
    private static readonly string[] NonConversationalFamilies =
        ["dall-e", "gpt-image", "tts-", "whisper", "moderation", "sora", "omni-moderation"];

    private static int? DimensionsOf(string remoteModelId)
    {
        return EmbeddingDimensionsByModel.TryGetValue(remoteModelId, out var dimensions) ? dimensions : null;
    }

    /// <summary>
    ///     Whether the id names a model this host can bind to an operation.
    /// </summary>
    /// <remarks>
    ///     A vendor's model list holds more than the models a chat client can call. Offered as chat models, an
    ///     image or a speech model is a binding an operator can pick and a review then fails on, and the
    ///     capabilities the description asserts for a chat model are wrong for every one of them.
    /// </remarks>
    /// <param name="remoteModelId">The identifier the endpoint listed.</param>
    internal static bool IsServable(string remoteModelId)
    {
        var normalized = remoteModelId.Trim();

        return !Array.Exists(
            NonConversationalFamilies,
            family => normalized.Contains(family, StringComparison.OrdinalIgnoreCase));
    }

    internal static ProviderDiscoveredModel Describe(string remoteModelId)
    {
        var normalized = remoteModelId.Trim();

        if (normalized.Contains("embedding", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderDiscoveredModel(
                normalized,
                normalized,
                [AiOperationKind.Embedding],
                [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
                "cl100k_base",
                MaxInputTokens: 8192,
                MaxContextTokens: null,
                EmbeddingDimensions: DimensionsOf(normalized));
        }

        return new ProviderDiscoveredModel(
            normalized,
            normalized,
            [AiOperationKind.Chat],
            [
                ProviderDeclaredProtocolModes.Auto,
                OpenAiProviderDriver.ResponsesProtocol,
                OpenAiProviderDriver.ChatCompletionsProtocol,
            ],
            SupportsStructuredOutput: true,
            SupportsToolUse: true);
    }

    private static HttpRequestMessage Request(ProviderEndpoint endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Address(endpoint));

        // The one authentication mode this family declares is a bearer key, which the vendor's own surface
        // reads.
        if (!string.IsNullOrWhiteSpace(endpoint.Secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret);
        }

        foreach (var (name, value) in endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    // The list sits beside the endpoint's own path rather than at the root of its host, because the vendor
    // surface is reached at a version prefix.
    private static Uri Address(ProviderEndpoint endpoint)
    {
        return ProviderEndpointAddress.For(endpoint, "models");
    }
}
