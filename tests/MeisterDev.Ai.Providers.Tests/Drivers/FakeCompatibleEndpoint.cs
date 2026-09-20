// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Stands in for an OpenAI-compatible endpoint so driver behaviour can be exercised over real HTTP
///     serialization rather than against a mocked client. It records every request body it is sent, which is how
///     assertions about what we put on the wire become possible at all.
/// </summary>
internal sealed class FakeCompatibleEndpoint : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    /// <summary>Bodies of the requests received, in order.</summary>
    public List<string> RequestBodies { get; } = [];

    /// <summary>
    ///     The address each request was sent to, in order, as the client library built it, so a test can assert
    ///     the path a driver addresses.
    /// </summary>
    public List<Uri?> RequestUris { get; } = [];

    /// <summary>
    ///     The headers of each request received, in order, including the content headers, which the client
    ///     library sets separately from the request headers. Keyed case-insensitively because a header name is
    ///     not case-sensitive. A header sent more than once is joined with <c>", "</c>, so a test asserting on a
    ///     repeated header has to expect that form.
    /// </summary>
    public List<IReadOnlyDictionary<string, string>> RequestHeaders { get; } = [];

    /// <summary>
    ///     The <c>Authorization</c> header of each request received, in order. Null for a request that carried
    ///     none.
    /// </summary>
    public List<string?> AuthorizationHeaders { get; } = [];

    /// <summary>Queues a raw JSON body to return for the next request, with a 200.</summary>
    public FakeCompatibleEndpoint Responds(string json)
    {
        return this.Responds(HttpStatusCode.OK, json);
    }

    /// <summary>Queues a status and a raw JSON body to return for the next request.</summary>
    public FakeCompatibleEndpoint Responds(HttpStatusCode status, string json)
    {
        this._responses.Enqueue((status, json));
        return this;
    }

    /// <summary>
    ///     Queues a DeepSeek-shaped chat completion: a standard assistant message carrying the non-standard
    ///     <c>reasoning_content</c> field those models return alongside their answer.
    /// </summary>
    public FakeCompatibleEndpoint RespondsWithReasoning(string content, string reasoningContent)
    {
        return this.Responds(
            $$"""
              {
                "id": "chatcmpl-fake",
                "object": "chat.completion",
                "created": 1770000000,
                "model": "deepseek-reasoner",
                "choices": [
                  {
                    "index": 0,
                    "finish_reason": "stop",
                    "message": {
                      "role": "assistant",
                      "content": {{System.Text.Json.JsonSerializer.Serialize(content)}},
                      "reasoning_content": {{System.Text.Json.JsonSerializer.Serialize(reasoningContent)}}
                    }
                  }
                ],
                "usage": { "prompt_tokens": 11, "completion_tokens": 7, "total_tokens": 18 }
              }
              """);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        this.RequestUris.Add(request.RequestUri);
        // Content headers live on the content, not the request, so a driver that sets one would otherwise be
        // invisible to a test asserting on headers. A name carried in both places keeps the values from both:
        // replacing the first with the second would hide one of them from the test that asserts on it.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentHeaders = request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>();
        foreach (var header in request.Headers.Concat(contentHeaders))
        {
            var joined = string.Join(", ", header.Value);
            headers[header.Key] = headers.TryGetValue(header.Key, out var recorded) ? $"{recorded}, {joined}" : joined;
        }

        this.RequestHeaders.Add(headers);
        this.AuthorizationHeaders.Add(
            request.Headers.TryGetValues("Authorization", out var authorization)
                ? string.Join(", ", authorization)
                : null);

        if (request.Content is not null)
        {
            this.RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        var (status, body) = this._responses.Count > 0 ? this._responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
