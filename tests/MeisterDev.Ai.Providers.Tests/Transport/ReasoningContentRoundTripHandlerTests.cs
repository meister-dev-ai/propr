// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     Covers the round-trip that keeps a reasoning model usable across turns. The provider hands back a
///     non-standard field and then demands it on the same turn next time; nothing above the wire can put it back,
///     so this is the layer that has to.
/// </summary>
public sealed class ReasoningContentRoundTripHandlerTests
{
    [Fact]
    public async Task AnAssistantTurnThatMadeToolCallsGetsItsReasoningBack()
    {
        var endpoint = new RecordingEndpoint();
        endpoint.Responds(ToolCallResponse("call_1", "let me look that up"));
        endpoint.Responds(PlainResponse("42"));
        using var client = Client(endpoint);

        await client.PostAsync("https://api.deepseek.com/v1/chat/completions", Json(FirstTurn()));
        await client.PostAsync("https://api.deepseek.com/v1/chat/completions", Json(SecondTurnWithToolCall("call_1")));

        var second = JsonDocument.Parse(endpoint.RequestBodies[1]);
        var assistant = second.RootElement.GetProperty("messages")[1];
        Assert.Equal("let me look that up", assistant.GetProperty("reasoning_content").GetString());
    }

    // The plain-text case: an assistant turn with no tool calls is matched on its exact text.
    [Fact]
    public async Task AnAssistantTurnWithNoToolCallsIsMatchedOnItsText()
    {
        var endpoint = new RecordingEndpoint();
        endpoint.Responds(PlainResponse("the answer is 42", "thinking about it"));
        endpoint.Responds(PlainResponse("ok"));
        using var client = Client(endpoint);

        await client.PostAsync("https://api.deepseek.com/v1/chat/completions", Json(FirstTurn()));
        await client.PostAsync(
            "https://api.deepseek.com/v1/chat/completions",
            Json(
                """
                {"messages":[{"role":"user","content":"one"},{"role":"assistant","content":"the answer is 42"},{"role":"user","content":"two"}]}
                """));

        var second = JsonDocument.Parse(endpoint.RequestBodies[1]);
        Assert.Equal("thinking about it", second.RootElement.GetProperty("messages")[1].GetProperty("reasoning_content").GetString());
    }

    // A provider that never sends the field must cost nothing and change nothing — this is every OpenAI and Azure
    // call in the product, so the handler staying out of the way is the common case, not the exception.
    [Fact]
    public async Task AProviderThatNeverSendsTheFieldHasItsRequestsLeftAlone()
    {
        var endpoint = new RecordingEndpoint();
        endpoint.Responds(PlainResponse("42"));
        endpoint.Responds(PlainResponse("43"));
        using var client = Client(endpoint);

        await client.PostAsync("https://api.openai.com/v1/chat/completions", Json(FirstTurn()));
        var secondBody = """
                         {"messages":[{"role":"user","content":"one"},{"role":"assistant","content":"42"},{"role":"user","content":"two"}]}
                         """;
        await client.PostAsync("https://api.openai.com/v1/chat/completions", Json(secondBody));

        Assert.DoesNotContain("reasoning_content", endpoint.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal(secondBody, endpoint.RequestBodies[1]);
    }

    // An assistant turn the provider never reasoned about must not be given someone else's chain of thought.
    [Fact]
    public async Task AnUnrecognisedAssistantTurnIsLeftAlone()
    {
        var endpoint = new RecordingEndpoint();
        endpoint.Responds(PlainResponse("remembered", "some reasoning"));
        endpoint.Responds(PlainResponse("ok"));
        using var client = Client(endpoint);

        await client.PostAsync("https://api.deepseek.com/v1/chat/completions", Json(FirstTurn()));
        await client.PostAsync(
            "https://api.deepseek.com/v1/chat/completions",
            Json(
                """
                {"messages":[{"role":"assistant","content":"something else entirely"}]}
                """));

        Assert.DoesNotContain("reasoning_content", endpoint.RequestBodies[1], StringComparison.Ordinal);
    }

    // A caller that already supplied the field keeps its own value: the handler restores what would otherwise be
    // lost rather than overwriting what a caller deliberately set.
    [Fact]
    public async Task AnAlreadyPresentFieldIsNotOverwritten()
    {
        var endpoint = new RecordingEndpoint();
        endpoint.Responds(PlainResponse("remembered", "handler reasoning"));
        endpoint.Responds(PlainResponse("ok"));
        using var client = Client(endpoint);

        await client.PostAsync("https://api.deepseek.com/v1/chat/completions", Json(FirstTurn()));
        await client.PostAsync(
            "https://api.deepseek.com/v1/chat/completions",
            Json(
                """
                {"messages":[{"role":"assistant","content":"remembered","reasoning_content":"caller reasoning"}]}
                """));

        var second = JsonDocument.Parse(endpoint.RequestBodies[1]);
        Assert.Equal("caller reasoning", second.RootElement.GetProperty("messages")[0].GetProperty("reasoning_content").GetString());
    }

    // The response still has to be readable by the client library after the handler has inspected it.
    [Fact]
    public async Task TheResponseBodyIsStillReadableAfterInspection()
    {
        var endpoint = new RecordingEndpoint();
        endpoint.Responds(PlainResponse("42", "thinking"));
        using var client = Client(endpoint);

        var response = await client.PostAsync("https://api.deepseek.com/v1/chat/completions", Json(FirstTurn()));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("42", payload.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    private static HttpClient Client(RecordingEndpoint endpoint)
    {
        return new HttpClient(new ReasoningContentRoundTripHandler { InnerHandler = endpoint });
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static string FirstTurn()
    {
        return """{"messages":[{"role":"user","content":"one"}]}""";
    }

    private static string SecondTurnWithToolCall(string toolCallId)
    {
        var toolCall = $$$"""
                          {"id":"{{{toolCallId}}}","type":"function","function":{"name":"read_file","arguments":"{}"}}
                          """;
        return $$$"""
                  {"messages":[
                    {"role":"user","content":"one"},
                    {"role":"assistant","content":null,"tool_calls":[{{{toolCall}}}]},
                    {"role":"tool","tool_call_id":"{{{toolCallId}}}","content":"file contents"}
                  ]}
                  """;
    }

    private static string PlainResponse(string content, string? reasoning = null)
    {
        var reasoningField = reasoning is null ? string.Empty : $",\"reasoning_content\":{JsonSerializer.Serialize(reasoning)}";
        return $$$"""
                  {"id":"chatcmpl-1","object":"chat.completion","choices":[
                    {"index":0,"finish_reason":"stop","message":{"role":"assistant","content":{{{JsonSerializer.Serialize(content)}}}{{{reasoningField}}}}}
                  ]}
                  """;
    }

    private static string ToolCallResponse(string toolCallId, string reasoning)
    {
        return $$$"""
                  {"id":"chatcmpl-1","object":"chat.completion","choices":[
                    {"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
                      "reasoning_content":{{{JsonSerializer.Serialize(reasoning)}}},
                      "tool_calls":[{"id":"{{{toolCallId}}}","type":"function","function":{"name":"read_file","arguments":"{}"}}]}}
                  ]}
                  """;
    }

    /// <summary>Records what reached the wire and replays queued responses, so both directions are assertable.</summary>
    private sealed class RecordingEndpoint : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new();

        public List<string> RequestBodies { get; } = [];

        public void Responds(string json)
        {
            this._responses.Enqueue(json);
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
}
