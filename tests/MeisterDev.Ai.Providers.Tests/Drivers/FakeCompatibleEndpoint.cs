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
    private readonly Queue<string> _responses = new();

    /// <summary>Bodies of the requests received, in order.</summary>
    public List<string> RequestBodies { get; } = [];

    /// <summary>Queues a raw JSON body to return for the next request.</summary>
    public FakeCompatibleEndpoint Responds(string json)
    {
        this._responses.Enqueue(json);
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
        if (request.Content is not null)
        {
            this.RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        var body = this._responses.Count > 0 ? this._responses.Dequeue() : "{}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
