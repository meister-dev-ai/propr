// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     Pins what the Anthropic driver puts on the wire and what it makes of the answer, against a fake endpoint
///     rather than assumptions. The Messages API differs from the OpenAI family in three ways that a translation
///     can silently get wrong — the system prompt, tool results, and the cache buckets in its usage payload — so
///     each is asserted on the actual request body.
/// </summary>
public sealed class AnthropicMessagesChatClientTests
{
    private static readonly ProviderEndpoint Endpoint =
        new(AiProviderKind.Anthropic, "https://api.anthropic.com/v1", AiAuthMode.XApiKey, "sk-ant-key");

    private static readonly ProviderModelDescriptor Model =
        new(Guid.NewGuid(), "claude-opus-5", [AiProtocolMode.Auto, AiProtocolMode.AnthropicMessages]);

    [Fact]
    public async Task TheAnswerAndItsIdentityComeBack()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("42"));

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "what is 6*7?")]);

        Assert.Equal("42", response.Text);
        Assert.Equal("claude-opus-5", response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task TheCredentialGoesInTheHeaderAnthropicReadsAndTheVersionIsAlwaysSent()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("sk-ant-key", Assert.Single(endpoint.Requests).Headers.GetValues("x-api-key").Single());
        Assert.Equal(
            AnthropicMessagesChatClient.AnthropicVersion,
            endpoint.Requests[0].Headers.GetValues("anthropic-version").Single());
        // A bearer token would be rejected by Anthropic, so it must not be sent.
        Assert.Null(endpoint.Requests[0].Headers.Authorization);
    }

    // The system prompt is a top-level field here, not a message. Left in the conversation, Anthropic rejects it.
    [Fact]
    public async Task TheSystemPromptIsLiftedOutOfTheConversation()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "You review code."),
            new ChatMessage(ChatRole.User, "hello"),
        ]);

        var body = JsonDocument.Parse(endpoint.Bodies[0]).RootElement;
        Assert.Equal("You review code.", body.GetProperty("system").GetString());
        var conversation = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(conversation);
        Assert.Equal("user", conversation[0].GetProperty("role").GetString());
    }

    // Anthropic requires max_tokens; omitting it is a 400 on every call.
    [Fact]
    public async Task AnOutputCapIsAlwaysSentEvenWhenTheCallerNamesNone()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.True(JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("max_tokens").GetInt32() > 0);
    }

    [Fact]
    public async Task AToolCallComesBackAsOneAndItsFinishReasonSaysSo()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(ToolUseResponse("toolu_1", "read_file", """{"path":"a.cs"}"""));

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "read a.cs")]);

        var call = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("toolu_1", call.CallId);
        Assert.Equal("read_file", call.Name);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    // A tool result is a USER turn carrying a tool_result block, not a role of its own. Sent as a "tool" role,
    // Anthropic rejects the request — this is the difference most likely to be mistranslated.
    [Fact]
    public async Task AToolResultIsSentAsAUserTurnCarryingAToolResultBlock()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("done"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "read a.cs"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("toolu_1", "read_file", null)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("toolu_1", "file contents")]),
        ]);

        var messages = JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("tool_use", messages[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("toolu_1", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
    }

    // Anthropic's input count EXCLUDES the cached portions, unlike the OpenAI family whose total contains them.
    // Left unadded, a cached-heavy call would report a fraction of the tokens it actually billed.
    [Fact]
    public async Task CacheBucketsAreAddedBackSoInputTokensMeanTheSameAsEverywhereElse()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(
            """
            {"id":"msg_1","model":"claude-opus-5","stop_reason":"end_turn",
             "content":[{"type":"text","text":"ok"}],
             "usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":300,"cache_creation_input_tokens":50}}
            """);

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(450, response.Usage!.InputTokenCount);
        Assert.Equal(20, response.Usage.OutputTokenCount);
        Assert.Equal(300, response.Usage.CachedInputTokenCount);

        // And the shared extractor reads the cache-write bucket through the per-provider key map.
        var normalized = ProviderUsageExtractor.FromResponse(response, AiProviderKind.Anthropic);
        Assert.Equal(50, normalized.CacheWriteTokens);
        Assert.Equal(450, normalized.InputTokens);
    }

    // Extended thinking arrives as its own block. Folded into the answer it would be indistinguishable from it.
    [Fact]
    public async Task AThinkingBlockBecomesReasoningRatherThanAnswerText()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(
            """
            {"id":"msg_1","model":"claude-opus-5","stop_reason":"end_turn",
             "content":[{"type":"thinking","thinking":"let me count"},{"type":"text","text":"42"}]}
            """);

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "6*7?")]);

        Assert.Equal("42", response.Text);
        Assert.Contains(
            "let me count",
            response.Messages[0].Contents.OfType<TextReasoningContent>().Select(part => part.Text));
    }

    // The status has to survive so the shared classifier can decide about retrying; 529 is Anthropic's own
    // overload signal and the driver treats it as transient.
    [Fact]
    public async Task AProviderRejectionCarriesItsStatusAndMessage()
    {
        var endpoint = new FakeAnthropicEndpoint().Fails(
            HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""");

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Contains("slow down", failure.Message, StringComparison.Ordinal);
    }

    // Caching is what makes the native path cheaper than the proxy path, and it only happens where a breakpoint
    // says so. The stable prefix of a review pass is re-sent on every file, so that is where the mark belongs.
    [Fact]
    public async Task ALargeSystemPromptIsMarkedAsTheCachedPrefix()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, new string('s', 6000)),
            new ChatMessage(ChatRole.User, "hello"),
        ]);

        var system = JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    // A prompt below the provider's own minimum is not cacheable at all, and a request may carry only four
    // breakpoints, so spending one there would cost a breakpoint and buy nothing.
    [Fact]
    public async Task AShortSystemPromptIsSentPlainWithoutSpendingABreakpoint()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "You review code."),
            new ChatMessage(ChatRole.User, "hello"),
        ]);

        Assert.Equal(
            JsonValueKind.String,
            JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("system").ValueKind);
    }

    [Fact]
    public async Task TheEndOfAnOngoingConversationIsMarkedSoTheNextTurnReadsItBack()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, new string('u', 5000)),
            new ChatMessage(ChatRole.Assistant, "and here is what I found"),
            new ChatMessage(ChatRole.User, "carry on"),
        ]);

        var messages = JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.True(messages[^1].GetProperty("content")[0].TryGetProperty("cache_control", out _));
        // Only the end is marked — a breakpoint caches everything before it, so a second mark inside the same
        // prefix would spend one for nothing.
        Assert.False(messages[0].GetProperty("content")[0].TryGetProperty("cache_control", out _));
    }

    [Fact]
    public async Task AFirstTurnIsNotMarkedBecauseThereIsNothingToReadBack()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, new string('u', 9000))]);

        var messages = JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("messages");
        Assert.False(messages[0].GetProperty("content")[0].TryGetProperty("cache_control", out _));
    }

    // Anthropic expresses reasoning as a token budget rather than a named level, and counts it against the same
    // cap as the answer — so a budget that was not added to the cap would truncate the visible answer.
    [Fact]
    public async Task AskingForReasoningTurnsOnExtendedThinkingAndLeavesRoomForTheAnswer()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            Reasoning(ProviderReasoningEffort.Medium, new ChatOptions { MaxOutputTokens = 4000 }));

        var body = JsonDocument.Parse(endpoint.Bodies[0]).RootElement;
        var budget = body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32();
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.True(budget >= 1024, "the provider rejects a budget below its own floor");
        Assert.True(body.GetProperty("max_tokens").GetInt32() > budget + 4000 - 1);
    }

    [Fact]
    public async Task MoreEffortBuysAStrictlyLargerThinkingBudget()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok")).Responds(TextResponse("ok"));
        var client = Client(endpoint);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], Reasoning(ProviderReasoningEffort.Low));
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], Reasoning(ProviderReasoningEffort.High));

        Assert.True(Budget(endpoint.Bodies[0]) < Budget(endpoint.Bodies[1]));
    }

    // The provider fixes the sampling temperature while thinking and rejects a request that sets both.
    [Fact]
    public async Task ExtendedThinkingSuppressesTheTemperatureTheProviderWouldRefuse()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok")).Responds(TextResponse("ok"));
        var client = Client(endpoint);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            Reasoning(ProviderReasoningEffort.High, new ChatOptions { Temperature = 0.2f }));
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Temperature = 0.2f });

        Assert.False(JsonDocument.Parse(endpoint.Bodies[0]).RootElement.TryGetProperty("temperature", out _));
        Assert.True(JsonDocument.Parse(endpoint.Bodies[1]).RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task AskingForNoReasoningLeavesThinkingOff()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            Reasoning(ProviderReasoningEffort.None));

        Assert.False(JsonDocument.Parse(endpoint.Bodies[0]).RootElement.TryGetProperty("thinking", out _));
    }

    // With thinking on, the provider verifies its own signature over the block and refuses an assistant turn
    // whose reasoning was dropped before its tool call.
    [Fact]
    public async Task AThinkingBlockGoesBackSignedAndAheadOfTheToolCallItLedTo()
    {
        var endpoint = new FakeAnthropicEndpoint()
            .Responds(
                """
                {"id":"msg_1","model":"claude-opus-5","stop_reason":"tool_use",
                 "content":[{"type":"thinking","thinking":"weighing it","signature":"sig-abc"},
                            {"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}]}
                """)
            .Responds(TextResponse("done"));
        var client = Client(endpoint);

        var first = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "read a.cs")],
            Reasoning(ProviderReasoningEffort.High));
        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "read a.cs"),
                first.Messages[0],
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("toolu_1", "file contents")]),
            ],
            Reasoning(ProviderReasoningEffort.High));

        var assistant = JsonDocument.Parse(endpoint.Bodies[1]).RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("sig-abc", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", assistant[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task RedactedThinkingIsHandedBackEvenThoughItCannotBeRead()
    {
        var endpoint = new FakeAnthropicEndpoint()
            .Responds(
                """
                {"id":"msg_1","model":"claude-opus-5","stop_reason":"end_turn",
                 "content":[{"type":"redacted_thinking","data":"encrypted-payload"},{"type":"text","text":"42"}]}
                """)
            .Responds(TextResponse("ok"));
        var client = Client(endpoint);

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "6*7?")], Reasoning(ProviderReasoningEffort.High));
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "6*7?"), first.Messages[0], new ChatMessage(ChatRole.User, "and 7*8?")],
            Reasoning(ProviderReasoningEffort.High));

        var assistant = JsonDocument.Parse(endpoint.Bodies[1]).RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("redacted_thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("encrypted-payload", assistant[0].GetProperty("data").GetString());
    }

    // Reasoning from somewhere else carries no signature the provider would accept, and sending it unsigned
    // fails the whole request — a turn without its reasoning is the lesser loss.
    [Fact]
    public async Task ReasoningThatCarriesNoProviderBlockIsNotInvented()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("thought elsewhere"), new TextContent("hi")]),
            new ChatMessage(ChatRole.User, "again"),
        ]);

        var assistant = JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("text", Assert.Single(assistant.EnumerateArray()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task StreamingIsRefusedRatherThanHalfImplemented()
    {
        var endpoint = new FakeAnthropicEndpoint().Responds(TextResponse("ok"));

        Assert.Throws<NotSupportedException>(() => Client(endpoint).GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        await Task.CompletedTask;
    }

    private static AnthropicMessagesChatClient Client(FakeAnthropicEndpoint endpoint)
    {
        return new AnthropicMessagesChatClient(new HttpClient(endpoint), Endpoint, Model);
    }

    /// <summary>Asks for reasoning the way a caller does: neutrally, through the per-client raw representation.</summary>
    private static ChatOptions Reasoning(ProviderReasoningEffort effort, ChatOptions? options = null)
    {
        options ??= new ChatOptions();
        options.RawRepresentationFactory = _ => new ProviderReasoningRequest(effort, CaptureReasoning: true);
        return options;
    }

    private static int Budget(string body)
    {
        return JsonDocument.Parse(body).RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32();
    }

    private static string TextResponse(string text)
    {
        var block = $$$"""{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}}""";
        return $$$"""
                  {"id":"msg_1","model":"claude-opus-5","stop_reason":"end_turn",
                   "content":[{{{block}}}],
                   "usage":{"input_tokens":11,"output_tokens":7}}
                  """;
    }

    private static string ToolUseResponse(string callId, string name, string inputJson)
    {
        var block = $$$"""{"type":"tool_use","id":"{{{callId}}}","name":"{{{name}}}","input":{{{inputJson}}}}""";
        return $$$"""
                  {"id":"msg_1","model":"claude-opus-5","stop_reason":"tool_use",
                   "content":[{{{block}}}],
                   "usage":{"input_tokens":11,"output_tokens":7}}
                  """;
    }

    /// <summary>Records what reached the wire and replays a queued answer.</summary>
    private sealed class FakeAnthropicEndpoint : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public FakeAnthropicEndpoint Responds(string json)
        {
            this._responses.Enqueue((HttpStatusCode.OK, json));
            return this;
        }

        public FakeAnthropicEndpoint Fails(HttpStatusCode status, string json)
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
}
