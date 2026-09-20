// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     What an endpoint's defaults do to a request a client library composed.
/// </summary>
/// <remarks>
///     A family composing its own address applies them itself. A client library composes its own requests from
///     the endpoint it was handed and knows nothing about either default, so both paths have to end up carrying
///     them or a gateway header reaches discovery and not the model call.
/// </remarks>
public sealed class ProviderEndpointDefaultsHandlerTests
{
    [Fact]
    public async Task ADefaultHeaderIsPutOnTheRequest()
    {
        var sent = await SendThroughAsync(
            Endpoint(headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Route"] = "eu" }),
            new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat"));

        Assert.Equal("eu", Assert.Single(sent.Headers.GetValues("X-Route")));
    }

    // The client library sets its own authorization from the credential the host configured, and a default is
    // what an operator added beside that.
    [Fact]
    public async Task AHeaderTheRequestAlreadyCarriesIsLeftAlone()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat");
        request.Headers.TryAddWithoutValidation("X-Route", "set-by-the-library");

        var sent = await SendThroughAsync(
            Endpoint(headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Route"] = "eu" }),
            request);

        Assert.Equal("set-by-the-library", Assert.Single(sent.Headers.GetValues("X-Route")));
    }

    // A provider that carries its key this way is configured this way, and a model call without it is
    // unauthenticated while discovery, which merges the defaults itself, works.
    [Fact]
    public async Task ADefaultQueryParameterIsMergedOntoTheAddress()
    {
        var sent = await SendThroughAsync(
            Endpoint(query: new Dictionary<string, string>(StringComparer.Ordinal) { ["api-version"] = "2026-01-01" }),
            new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat?stream=true"));

        Assert.Contains("stream=true", sent.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("api-version=2026-01-01", sent.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointWithNoDefaultsChangesNothing()
    {
        var sent = await SendThroughAsync(
            Endpoint(),
            new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat?stream=true"));

        Assert.Equal("https://api.example.com/v1/chat?stream=true", sent.RequestUri!.ToString());
        Assert.Empty(sent.Headers);
    }

    private static ProviderEndpoint Endpoint(
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, string>? query = null)
    {
        return new ProviderEndpoint("tests/defaults", "https://api.example.com/v1", "tests/defaults:ApiKey")
        {
            DefaultHeaders = headers,
            DefaultQueryParams = query,
        };
    }

    private static async Task<HttpRequestMessage> SendThroughAsync(ProviderEndpoint endpoint, HttpRequestMessage request)
    {
        var recorder = new RecordingHandler();
        var handler = new ProviderEndpointDefaultsHandler(endpoint) { InnerHandler = recorder };

        using var client = new HttpClient(handler);
        using var response = await client.SendAsync(request, CancellationToken.None);

        return recorder.Sent!;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.Sent = request;

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
