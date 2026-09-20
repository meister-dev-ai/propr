// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.LiteLlmAddIn;

/// <summary>
///     Reads the model list a LiteLLM gateway publishes at <c>/models</c> as <c>{ "data": [ { "id": … } ] }</c>.
/// </summary>
/// <remarks>
///     The list is the only administrative call this family makes, and it doubles as the verification call: a
///     gateway that answers it with the configured virtual key is a gateway a review can use.
/// </remarks>
internal static class LiteLlmModels
{
    /// <summary>What a gateway answered when asked for its models.</summary>
    /// <param name="Status">The status the gateway replied with.</param>
    /// <param name="Models">The model identifiers it listed, empty when it listed none or refused.</param>
    /// <param name="Detail">What it said when it refused, or null when it did not.</param>
    internal readonly record struct Listing(HttpStatusCode Status, IReadOnlyList<string> Models, string? Detail);

    /// <summary>Asks a gateway for its models.</summary>
    /// <param name="endpoint">Where to reach the gateway, and what to authenticate with.</param>
    /// <param name="ct">Cancels the call.</param>
    internal static async Task<Listing> ListAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var request = Request(endpoint);
        using var client = LiteLlmTransport.Client(endpoint, ProviderHttpPurpose.Admin);
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
    ///     What a model identifier says about the model behind it, for a gateway that publishes identifiers and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     A gateway lists an identifier per model and no capabilities, so what is offered here is a starting
    ///     point an operator corrects rather than a statement about the model behind it.
    /// </remarks>
    /// <param name="remoteModelId">The identifier the gateway listed.</param>
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
            [
                ProviderDeclaredProtocolModes.Auto,
                LiteLlmProviderDriver.ResponsesProtocol,
                LiteLlmProviderDriver.ChatCompletionsProtocol,
            ],
            SupportsStructuredOutput: true,
            SupportsToolUse: true);
    }

    private static HttpRequestMessage Request(ProviderEndpoint endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Address(endpoint));

        // A gateway authenticates its callers with a virtual key sent as a bearer token, whatever the upstream
        // provider behind it authenticates with.
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

    // The list sits beside the gateway's own path rather than at the root of its host, because a gateway is
    // commonly reached at a prefix an operator chose.
    private static Uri Address(ProviderEndpoint endpoint)
    {
        return ProviderEndpointAddress.For(endpoint, "models");
    }
}
