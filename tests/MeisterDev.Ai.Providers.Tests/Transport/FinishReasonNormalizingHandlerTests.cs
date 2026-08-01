// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     Covers the repair that keeps a non-conforming completion readable. The client library's finish-reason enum
///     is closed, so a value outside it fails the call during deserialization; this is the layer that has to fix
///     the body before the library ever parses it.
/// </summary>
public sealed class FinishReasonNormalizingHandlerTests
{
    [Fact]
    public async Task ANullFinishReasonOnAToolCallBecomesToolCalls()
    {
        var body = await Send(Completion("null", ToolCallMessage));

        Assert.Equal("tool_calls", FinishReasonOf(body));
    }

    [Fact]
    public async Task ANullFinishReasonOnAPlainAnswerBecomesStop()
    {
        var body = await Send(Completion("null", PlainMessage));

        Assert.Equal("stop", FinishReasonOf(body));
    }

    // A vendor term is taken at its word rather than inferred, so a truncation stays a truncation.
    [Fact]
    public async Task AForeignFinishReasonIsMappedToItsOpenAiEquivalent()
    {
        Assert.Equal("stop", FinishReasonOf(await Send(Completion("\"end_turn\"", PlainMessage))));
        Assert.Equal("length", FinishReasonOf(await Send(Completion("\"max_tokens\"", PlainMessage))));
        Assert.Equal("tool_calls", FinishReasonOf(await Send(Completion("\"tool_use\"", ToolCallMessage))));
    }

    // An unknown word with no obvious equivalent falls back to reading the message, which is the honest answer.
    [Fact]
    public async Task AnUnrecognisedFinishReasonIsInferredFromTheMessage()
    {
        Assert.Equal("tool_calls", FinishReasonOf(await Send(Completion("\"whatever\"", ToolCallMessage))));
        Assert.Equal("stop", FinishReasonOf(await Send(Completion("\"whatever\"", PlainMessage))));
    }

    // A conforming provider is the common case, so the body has to come back byte for byte.
    [Fact]
    public async Task AConformingCompletionIsLeftByteForByteAlone()
    {
        var original = Completion("\"stop\"", PlainMessage);

        Assert.Equal(original, await Send(original));
    }

    // An absent field is legal and the client library reads it as a stop, so writing one in would be a change
    // with no reader.
    [Fact]
    public async Task AnAbsentFinishReasonIsLeftAbsent()
    {
        var original = """{"choices":[{"index":0,"message":""" + PlainMessage + "}]}";

        Assert.Equal(original, await Send(original));
    }

    // Every choice is repaired, not just the first, because n>1 is a supported request shape.
    [Fact]
    public async Task EveryChoiceIsRepaired()
    {
        var body = await Send(
            $$"""
              {"choices":[
                {"index":0,"finish_reason":null,"message":{{PlainMessage}}},
                {"index":1,"finish_reason":null,"message":{{ToolCallMessage}}}
              ]}
              """);

        var choices = JsonDocument.Parse(body).RootElement.GetProperty("choices");
        Assert.Equal("stop", choices[0].GetProperty("finish_reason").GetString());
        Assert.Equal("tool_calls", choices[1].GetProperty("finish_reason").GetString());
    }

    // A stream carries a null finish reason on every chunk but the last, so rewriting one would invent a
    // completion the model never signalled. Content type is what keeps the handler out.
    [Fact]
    public async Task AStreamedResponseIsLeftAlone()
    {
        var chunk = """data: {"choices":[{"index":0,"finish_reason":null,"delta":{"content":"hi"}}]}""";

        Assert.Equal(chunk, await Send(chunk, "text/event-stream"));
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsLeftAlone()
    {
        const string body = "finish_reason but not actually json";

        Assert.Equal(body, await Send(body));
    }

    private const string PlainMessage = """{"role":"assistant","content":"hello"}""";

    private const string ToolCallMessage =
        """{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{}"}}]}""";

    private static string Completion(string finishReasonJson, string message)
    {
        return $$"""
                 {"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"finish_reason":{{finishReasonJson}},"message":{{message}}}]}
                 """;
    }

    private static string? FinishReasonOf(string body)
    {
        return JsonDocument.Parse(body).RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString();
    }

    private static async Task<string> Send(string responseBody, string mediaType = "application/json")
    {
        using var client = new HttpClient(new FinishReasonNormalizingHandler { InnerHandler = new StubEndpoint(responseBody, mediaType) });

        var response = await client.GetAsync("https://opencode.ai/zen/v1/chat/completions");
        return await response.Content.ReadAsStringAsync();
    }

    private sealed class StubEndpoint(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, mediaType),
                });
        }
    }
}
