// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn.Tests;

/// <summary>
///     Pins what the Google driver puts on the wire and what it makes of the answer, against a fake endpoint.
///     The generateContent protocol renames the assistant, separates the system prompt, carries tool results as
///     user turns and flags thinking inside ordinary parts — each is asserted on the actual request body.
/// </summary>
public sealed class GoogleGenerateContentChatClientTests
{
    private static readonly ProviderModelDescriptor Model =
        new(Guid.NewGuid(), "gemini-3-pro", [ProviderDeclaredProtocolModes.Auto, GoogleVertexProviderDriver.GenerateContentProtocol]);

    [Fact]
    public async Task TheAnswerAndItsIdentityComeBack()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("42"));

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "what is 6*7?")]);

        Assert.Equal("42", response.Text);
        Assert.Equal("gemini-3-pro", response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    // The Gemini API reads its key from a header of its own; a bearer token is only right on Vertex.
    [Fact]
    public async Task TheGeminiApiIsAddressedByModelAndAuthenticatedWithItsKeyHeader()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var request = Assert.Single(endpoint.Requests);
        Assert.Equal("/v1beta/models/gemini-3-pro:generateContent", request.RequestUri!.AbsolutePath);
        Assert.Equal("gemini-key", request.Headers.GetValues(GoogleCredentialSource.ApiKeyHeaderName).Single());
    }

    // On Vertex the model lives inside one project and location, and that path is the residency guarantee: a
    // request that lost the project would run somewhere the customer never approved.
    [Fact]
    public async Task VertexAddressesTheModelInsideTheProjectAndLocationTheProfileNames()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("ok"));
        var vertex = new ProviderEndpoint(
            "meisterdev/googleVertex",
            "https://europe-west4-aiplatform.googleapis.com",
            GoogleVertexProviderDriver.GcpAdcAuth,
            "{}")
        {
            DefaultQueryParams = new Dictionary<string, string> { ["project"] = "meister-dev-prod" },
        };

        // A real Google credential is not what this asserts, and minting one would need a Google account.
        await Client(endpoint, vertex, new StubCredentials()).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(
            "/v1/projects/meister-dev-prod/locations/europe-west4/publishers/google/models/gemini-3-pro:generateContent",
            endpoint.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task TheSystemPromptIsLiftedIntoItsOwnInstruction()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "You review code."),
            new ChatMessage(ChatRole.User, "hello"),
        ]);

        var body = JsonDocument.Parse(endpoint.Bodies[0]).RootElement;
        Assert.Equal("You review code.", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Single(body.GetProperty("contents").EnumerateArray());
    }

    // The assistant is called "model" here, and a tool result is a user turn carrying a functionResponse part.
    [Fact]
    public async Task AToolExchangeUsesTheRoleNamesGoogleExpects()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("done"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "read a.cs"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("read_file", "read_file", null)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read_file", "file contents")]),
        ]);

        var contents = JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("contents").EnumerateArray().ToList();
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("read_file", contents[1].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("name").GetString());
        Assert.Equal("user", contents[2].GetProperty("role").GetString());
        Assert.Equal("read_file", contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString());
    }

    // Gemini matches a tool result to the call it answers by the function's name. A real call id is opaque and
    // is not the name, so writing it into 'name' put an identifier where the model reads a function and the
    // result answered a call it never made. The id still travels, in the field the protocol has for it.
    [Fact]
    public async Task AToolResultNamesTheFunctionAndCarriesTheCallIdBesideIt()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("done"));

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "read a.cs"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-7f3a", "read_file", null)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-7f3a", "file contents")]),
        ]);

        var response = JsonDocument.Parse(endpoint.Bodies[0]).RootElement
            .GetProperty("contents")[2]
            .GetProperty("parts")[0]
            .GetProperty("functionResponse");

        Assert.Equal("read_file", response.GetProperty("name").GetString());
        Assert.Equal("call-7f3a", response.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AToolCallComesBackAsOneAndItsFinishReasonSaysSo()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(
            """
            {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"read_file","args":{"path":"a.cs"}}}]},
             "finishReason":"STOP"}],"modelVersion":"gemini-3-pro"}
            """);

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "read a.cs")]);

        var call = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("read_file", call.Name);
        // Google names a call rather than identifying it, unless several are in flight — so the name is the id.
        Assert.Equal("read_file", call.CallId);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    // Thinking arrives as an ordinary text part wearing a flag. Read as text it would be indistinguishable from
    // the answer, and the signature that comes with it has to go back on the next turn.
    [Fact]
    public async Task AThoughtPartBecomesReasoningAndGoesBackSigned()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(
                """
                {"candidates":[{"content":{"role":"model","parts":[
                    {"thought":true,"text":"weighing it","thoughtSignature":"sig-abc"},
                    {"functionCall":{"name":"read_file","args":{}}}]},"finishReason":"STOP"}],
                 "modelVersion":"gemini-3-pro"}
                """)
            .Responds(TextResponse("done"));
        var client = Client(endpoint);

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "read a.cs")]);
        Assert.Contains("weighing it", first.Messages[0].Contents.OfType<TextReasoningContent>().Select(part => part.Text));
        Assert.DoesNotContain("weighing it", first.Text);

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "read a.cs"),
            first.Messages[0],
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read_file", "contents")]),
        ]);

        var parts = JsonDocument.Parse(endpoint.Bodies[1]).RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.True(parts[0].GetProperty("thought").GetBoolean());
        Assert.Equal("sig-abc", parts[0].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task AskingForReasoningSetsAThinkingBudgetAndAsksForTheThoughtsBack()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions
            {
                RawRepresentationFactory = _ => new ProviderReasoningRequest(ProviderReasoningEffort.Medium, true),
            });

        var thinking = JsonDocument.Parse(endpoint.Bodies[0]).RootElement
            .GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.True(thinking.GetProperty("thinkingBudget").GetInt32() > 0);
        Assert.True(thinking.GetProperty("includeThoughts").GetBoolean());
    }

    [Fact]
    public async Task AskingForNoReasoningLeavesTheThinkingConfigOff()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("ok"));

        await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], new ChatOptions { Temperature = 0.2f });

        var generation = JsonDocument.Parse(endpoint.Bodies[0]).RootElement.GetProperty("generationConfig");
        Assert.False(generation.TryGetProperty("thinkingConfig", out _));
    }

    // Google counts thinking outside the candidate total while still billing it as output, and its prompt count
    // already contains the cached portion. Both facts are the driver's mapping to act on, so what the client
    // reports is what Google reported.
    [Fact]
    public async Task GooglesOwnCountsReachTheSeamWhereTheDriverMapsThem()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}],
             "modelVersion":"gemini-3-pro",
             "usageMetadata":{"promptTokenCount":400,"candidatesTokenCount":20,"cachedContentTokenCount":300,
                              "thoughtsTokenCount":50,"totalTokenCount":470}}
            """);

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        // The thinking tokens sit outside the candidate count here and the driver adds them into the output
        // total, so this is Google's own shape arriving at the seam where the mapping reads it.
        Assert.Equal(400, response.Usage!.InputTokenCount);
        Assert.Equal(20, response.Usage.OutputTokenCount);
        Assert.Equal(300, response.Usage.CachedInputTokenCount);
        Assert.Equal(50, response.Usage.ReasoningTokenCount);
    }

    [Fact]
    public async Task AProviderRejectionCarriesItsStatusAndMessage()
    {
        var endpoint = new FakeGoogleEndpoint().Fails(
            HttpStatusCode.TooManyRequests,
            """{"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED"}}""");

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Contains("Quota exceeded", failure.Message, StringComparison.Ordinal);
    }

    // A gateway in front of Google answers with its own shape, and the error message can then be an object, a
    // number or no JSON at all. Reading it as a string would raise from inside the code that describes the
    // rejection, and the status the provider answered with would be lost with it.
    [Theory]
    [InlineData("""{"error":{"code":502,"message":{"detail":"upstream closed"}}}""", "upstream closed")]
    [InlineData("""{"error":{"code":502,"message":502}}""", "502")]
    [InlineData("<html><body>502 Bad Gateway</body></html>", "502 Bad Gateway")]
    public async Task ARejectionWhoseMessageIsNotAStringStillCarriesItsStatus(string body, string expected)
    {
        var endpoint = new FakeGoogleEndpoint().Fails(HttpStatusCode.BadGateway, body);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    // Google refuses a prompt with 200, no candidates, and the reason under promptFeedback. Unread, that is an
    // empty assistant turn with no finish reason, which a review pass cannot tell apart from a model that found
    // nothing to say.
    [Theory]
    [InlineData("SAFETY")]
    [InlineData("OTHER")]
    [InlineData("A_REASON_THIS_BUILD_HAS_NEVER_SEEN")]
    public async Task ARefusedPromptIsReportedAsFilteredAndNamesTheReason(string reason)
    {
        var endpoint = new FakeGoogleEndpoint().Responds($$"""{"promptFeedback":{"blockReason":"{{reason}}"},"modelVersion":"gemini-3-pro"}""");

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(ChatFinishReason.ContentFilter, response.FinishReason);
        Assert.Equal(reason, response.AdditionalProperties?[GoogleGenerateContentChatClient.PromptBlockReason]);
    }

    // A model that answered and simply produced no parts is not a refusal, and reporting one would send an
    // operator looking for a safety rule that was never applied.
    [Fact]
    public async Task AnAnsweredPromptWithNoPartsIsNotReportedAsFiltered()
    {
        var endpoint = new FakeGoogleEndpoint().Responds("""{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}]}""");

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Null(response.AdditionalProperties);
    }

    [Fact]
    public async Task StreamingIsRefusedRatherThanHalfImplemented()
    {
        var endpoint = new FakeGoogleEndpoint().Responds(TextResponse("ok"));

        Assert.Throws<NotSupportedException>(() => Client(endpoint).GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        await Task.CompletedTask;
    }

    private static GoogleGenerateContentChatClient Client(
        FakeGoogleEndpoint endpoint,
        ProviderEndpoint? provider = null,
        IGoogleCredentialSource? credentials = null)
    {
        provider ??= new ProviderEndpoint(
            "meisterdev/googleVertex",
            "https://generativelanguage.googleapis.com",
            GoogleVertexProviderDriver.ApiKeyAuth,
            "gemini-key");

        return new GoogleGenerateContentChatClient(
            new FakeProviderHostContext(endpoint).Http,
            credentials ?? new GoogleCredentialSource(),
            provider,
            Model);
    }

    /// <summary>Stands in for a Google credential where what is under test is the request, not the token.</summary>
    private sealed class StubCredentials : IGoogleCredentialSource
    {
        public Task AuthenticateAsync(HttpRequestMessage request, ProviderEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "stub-token");
            return Task.CompletedTask;
        }
    }

    private static string TextResponse(string text)
    {
        var part = $$$"""{"text":{{{JsonSerializer.Serialize(text)}}}}""";
        return $$$"""
                  {"candidates":[{"content":{"role":"model","parts":[{{{part}}}]},"finishReason":"STOP"}],
                   "modelVersion":"gemini-3-pro",
                   "usageMetadata":{"promptTokenCount":11,"candidatesTokenCount":7,"totalTokenCount":18}}
                  """;
    }
}
