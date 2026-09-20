// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.AnthropicAddIn;

/// <summary>
///     Reads the model list an Anthropic endpoint publishes at <c>/models</c> as
///     <c>{ "data": [ { "id": … } ] }</c>.
/// </summary>
/// <remarks>
///     The list is the only administrative call this family makes, and it doubles as the verification call: an
///     endpoint that answers it with the configured credential is an endpoint a review can use.
/// </remarks>
internal static class AnthropicModels
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
        using var client = AnthropicTransport.Client(endpoint, ProviderHttpPurpose.Admin);
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
        // part that was read would present an incomplete list as the endpoint's own.
        if (truncated)
        {
            throw new HttpRequestException(
                $"The provider's model list is longer than the {ProviderResponseBody.MaximumDocumentBytes} bytes "
                + "this host reads from a response body.");
        }

        return new Listing(response.StatusCode, Parse(payload), null);
    }

    /// <summary>
    ///     What a model identifier says about the model behind it, for an endpoint that publishes identifiers and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     Every model on this surface is a chat model reached over the Messages protocol: Anthropic serves no
    ///     embedding models, and structured output is not a mode its API offers, so a discovered model is offered
    ///     for chat and tool use and nothing more.
    /// </remarks>
    /// <param name="remoteModelId">The identifier the endpoint listed.</param>
    internal static ProviderDiscoveredModel Describe(string remoteModelId)
    {
        var normalized = remoteModelId.Trim();

        return new ProviderDiscoveredModel(
            normalized,
            normalized,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, AnthropicProviderDriver.MessagesProtocol],
            SupportsStructuredOutput: false,
            SupportsToolUse: true);
    }

    private static HttpRequestMessage Request(ProviderEndpoint endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Address(endpoint));

        AnthropicMessagesChatClient.ApplyRequiredHeaders(request, endpoint);

        return request;
    }

    // The list sits beside the endpoint's own path rather than at the root of its host, because the Messages
    // protocol is also served by gateways and proxies reached at a prefix an operator chose.
    private static Uri Address(ProviderEndpoint endpoint)
    {
        var builder = new UriBuilder(new Uri(endpoint.BaseUrl, UriKind.Absolute));
        builder.Path = $"{builder.Path.TrimEnd('/')}/models";

        if (endpoint.DefaultQueryParams is { Count: > 0 } parameters)
        {
            builder.Query = string.Join(
                "&",
                parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        return builder.Uri;
    }

    private static IReadOnlyList<string> Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. data.EnumerateArray()
                .Select(entry => entry.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
