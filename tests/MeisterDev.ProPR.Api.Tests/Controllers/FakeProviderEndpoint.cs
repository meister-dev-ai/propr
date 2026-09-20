// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     Stands in for every AI provider the test host can reach, so a request a driver builds from a stored
///     profile can be asserted instead of being sent to the provider's real address.
/// </summary>
/// <remarks>
///     Installed as the primary handler of the named clients every driver takes its <see cref="HttpClient" />
///     from, which is the one seam all of them share: the AWS SDK, the OpenAI client library and the drivers
///     that build requests by hand all end up here. Without it a test that saves a credential and verifies the
///     profile reaches the provider's public endpoint, which makes the suite depend on internet access and
///     proves nothing about what was sent.
/// </remarks>
internal sealed class FakeProviderEndpoint : HttpMessageHandler
{
    // Every model list a driver reads here is empty, in the three shapes the drivers parse: 'data' for the
    // OpenAI-compatible families, 'models' for Google, 'modelSummaries' for the Bedrock control plane. One body
    // serves all of them because each reads only its own key.
    private const string EmptyModelListBody =
        """{ "object": "list", "data": [], "models": [], "modelSummaries": [] }""";

    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>The requests received since the last <see cref="Clear" />, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (this._gate)
            {
                return [.. this._requests];
            }
        }
    }

    /// <summary>Forgets what was recorded, so one test does not read another test's traffic.</summary>
    public void Clear()
    {
        lock (this._gate)
        {
            this._requests.Clear();
        }
    }

    /// <summary>The single request received, failing when a test saw none or more than one.</summary>
    public RecordedRequest Single()
    {
        return Assert.Single(this.Requests);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Content headers live on the content and request headers on the request, and a driver may use either,
        // so both are recorded. A name carried in both places keeps all of its values.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentHeaders = request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>();
        foreach (var header in request.Headers.Concat(contentHeaders))
        {
            var joined = string.Join(", ", header.Value);
            headers[header.Key] = headers.TryGetValue(header.Key, out var existing)
                ? $"{existing}, {joined}"
                : joined;
        }

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (this._gate)
        {
            this._requests.Add(new RecordedRequest(request.Method, request.RequestUri, headers, body));
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EmptyModelListBody, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    ///     Installs this endpoint on one client's pipeline without handing that pipeline ownership of it.
    /// </summary>
    /// <remarks>
    ///     The client factory disposes the primary handler it built when that client's handler lifetime expires.
    ///     One endpoint answers three named clients, so the first expiry would dispose an instance the other two
    ///     are still sending through, and their next request would fail on a disposed handler.
    /// </remarks>
    public HttpMessageHandler AsSharedPrimaryHandler()
    {
        return new SharedPrimaryHandler(this);
    }

    // Forwards to an endpoint it does not own. Dispose is not passed on, which is the whole point: the base
    // DelegatingHandler disposes its inner handler, and this one outlives every pipeline it is installed on.
    private sealed class SharedPrimaryHandler(HttpMessageHandler shared) : DelegatingHandler(shared)
    {
        protected override void Dispose(bool disposing)
        {
            // Intentionally not disposing the inner handler.
        }
    }

    /// <summary>One request as it left the driver.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Uri">The address the driver built, including its query string.</param>
    /// <param name="Headers">Request and content headers, keyed case-insensitively.</param>
    /// <param name="Body">The request body, or <see langword="null" /> for a request that carried none.</param>
    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        IReadOnlyDictionary<string, string> Headers,
        string? Body)
    {
        /// <summary>Reads one header, or <see langword="null" /> when the request did not carry it.</summary>
        /// <param name="name">The header name.</param>
        public string? Header(string name)
        {
            return this.Headers.TryGetValue(name, out var value) ? value : null;
        }
    }
}
