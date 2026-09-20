// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn.Tests;

/// <summary>Records what reached the wire and replays a queued answer.</summary>
internal sealed class FakeGoogleEndpoint : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public FakeGoogleEndpoint Responds(string json)
    {
        this._responses.Enqueue((HttpStatusCode.OK, json));
        return this;
    }

    public FakeGoogleEndpoint Fails(HttpStatusCode status, string json)
    {
        this._responses.Enqueue((status, json));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        this.Requests.Add(request);
        if (request.Content is not null)
        {
            this.Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        var (status, body) = this._responses.Count > 0 ? this._responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
