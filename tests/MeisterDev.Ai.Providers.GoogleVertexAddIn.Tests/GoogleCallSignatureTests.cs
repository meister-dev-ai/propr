// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn.Tests;

/// <summary>
///     Covers the signature Google issues over a function call. A later turn whose calls come back without it is
///     refused, so it has to survive from the response that carried it to the request that repeats it.
/// </summary>
public sealed class GoogleCallSignatureTests
{
    private static readonly ProviderModelDescriptor Model = new(
        Guid.NewGuid(),
        "gemini-3.8-flash",
        [ProviderDeclaredProtocolModes.Auto, GoogleVertexProviderDriver.GenerateContentProtocol]);

    [Fact]
    public async Task ASignedCallIsRepeatedWithItsSignature()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(SignedCall("read_file", "sig-one"))
            .Responds(TextResponse("done"));

        var signature = Assert.Single(await CallThenAnswerAsync(endpoint));

        Assert.Equal("sig-one", signature);
    }

    // The signature has to reach the request through whatever the review loop carried the call in, and a review
    // executed on a runner carries it as serialized JSON. Anything held only in memory would be gone by here.
    [Fact]
    public async Task ASignatureSurvivesTheCallBeingSerializedAndReadBack()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(SignedCall("read_file", "sig-relayed"))
            .Responds(TextResponse("done"));
        var client = Client(endpoint);

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "read a.cs")]);

        var relayed = JsonSerializer.Deserialize<ChatMessage>(
            JsonSerializer.Serialize(first.Messages[0], AIJsonUtilities.DefaultOptions),
            AIJsonUtilities.DefaultOptions)!;

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "read a.cs"),
            relayed,
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read_file", "contents")]),
        ]);

        Assert.Equal("sig-relayed", Assert.Single(SignaturesSentOn(endpoint.Bodies[1])));
    }

    // The signature sits beside the call on the part, not inside the call. Google reads only the part-level one,
    // so a signature written one level down is both absent where it is looked for and present where it is not.
    [Fact]
    public async Task TheSignatureIsASiblingOfTheCallAndNotAMemberOfIt()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(SignedCall("read_file", "sig-level"))
            .Responds(TextResponse("done"));

        await CallThenAnswerAsync(endpoint);

        var part = JsonDocument.Parse(endpoint.Bodies[1]).RootElement
            .GetProperty("contents")[1].GetProperty("parts")[0];

        Assert.Equal("sig-level", part.GetProperty("thoughtSignature").GetString());
        Assert.False(part.GetProperty("functionCall").TryGetProperty("thoughtSignature", out _));
        Assert.False(part.GetProperty("functionCall").TryGetProperty("thought_signature", out _));
    }

    // A model that issues no signature must not have an empty one invented for it, which would be a field
    // Google reads as a signature that does not verify.
    [Fact]
    public async Task AnUnsignedCallIsRepeatedWithNoSignatureField()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(
                """
                {"candidates":[{"content":{"role":"model","parts":[
                    {"functionCall":{"name":"read_file","args":{"path":"a.cs"}}}]},"finishReason":"STOP"}],
                 "modelVersion":"gemini-2.5-flash"}
                """)
            .Responds(TextResponse("done"));

        Assert.Empty(await CallThenAnswerAsync(endpoint));
    }

    // The refusal names the protobuf field while the payload carries the camel-cased one. The part is replayed
    // as it arrived rather than read field by field, so whichever spelling Google used comes back unchanged and
    // neither has to be recognised here.
    [Theory]
    [InlineData("thoughtSignature")]
    [InlineData("thought_signature")]
    public async Task ThePartComesBackInTheSpellingItArrivedIn(string field)
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(
                $$$"""
                   {"candidates":[{"content":{"role":"model","parts":[
                       {"functionCall":{"name":"read_file","args":{}},"{{{field}}}":"sig-either"}]},"finishReason":"STOP"}],
                    "modelVersion":"gemini-3.8-flash"}
                   """)
            .Responds(TextResponse("done"));

        await CallThenAnswerAsync(endpoint);

        var part = JsonDocument.Parse(endpoint.Bodies[1]).RootElement
            .GetProperty("contents")[1].GetProperty("parts")[0];

        Assert.Equal("sig-either", part.GetProperty(field).GetString());
    }

    // Each call keeps the signature issued over it, so a second turn does not repeat the first one's.
    [Fact]
    public async Task EachCallCarriesItsOwnSignatureAcrossTurns()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(SignedCall("read_file", "sig-first"))
            .Responds(SignedCall("search_code", "sig-second"))
            .Responds(TextResponse("done"));
        var client = Client(endpoint);

        var turn = new List<ChatMessage> { new(ChatRole.User, "read a.cs") };

        var first = await client.GetResponseAsync(turn);
        turn.Add(first.Messages[0]);
        turn.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read_file", "contents")]));

        var second = await client.GetResponseAsync(turn);
        turn.Add(second.Messages[0]);
        turn.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("search_code", "matches")]));

        await client.GetResponseAsync(turn);

        Assert.Equal(["sig-first"], SignaturesSentOn(endpoint.Bodies[1]));
        Assert.Equal(["sig-first", "sig-second"], SignaturesSentOn(endpoint.Bodies[2]));
    }

    // A thought and the call it led to are both signed, and the thought still goes back first.
    [Fact]
    public async Task AThoughtStillPrecedesTheCallAndBothKeepTheirSignatures()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(
                """
                {"candidates":[{"content":{"role":"model","parts":[
                    {"thought":true,"text":"weighing it","thoughtSignature":"sig-thought"},
                    {"functionCall":{"name":"read_file","args":{}},"thoughtSignature":"sig-call"}]},
                  "finishReason":"STOP"}],"modelVersion":"gemini-3.8-flash"}
                """)
            .Responds(TextResponse("done"));

        await CallThenAnswerAsync(endpoint);

        var parts = JsonDocument.Parse(endpoint.Bodies[1]).RootElement
            .GetProperty("contents")[1].GetProperty("parts");

        Assert.True(parts[0].GetProperty("thought").GetBoolean());
        Assert.Equal("sig-thought", parts[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("sig-call", parts[1].GetProperty("thoughtSignature").GetString());
    }

    // Several calls in flight are identified rather than named, and the signature travels beside that id rather
    // than displacing it.
    [Fact]
    public async Task AnIdentifiedCallKeepsBothItsIdAndItsSignature()
    {
        var endpoint = new FakeGoogleEndpoint()
            .Responds(
                """
                {"candidates":[{"content":{"role":"model","parts":[
                    {"functionCall":{"id":"call-7","name":"read_file","args":{}},"thoughtSignature":"sig-id"}]},
                  "finishReason":"STOP"}],"modelVersion":"gemini-3.8-flash"}
                """)
            .Responds(TextResponse("done"));

        await CallThenAnswerAsync(endpoint);

        var part = JsonDocument.Parse(endpoint.Bodies[1]).RootElement
            .GetProperty("contents")[1].GetProperty("parts")[0];

        Assert.Equal("call-7", part.GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("sig-id", part.GetProperty("thoughtSignature").GetString());
    }

    /// <summary>Runs one call-and-answer exchange and returns the signatures the second request carried.</summary>
    private static async Task<IReadOnlyList<string>> CallThenAnswerAsync(FakeGoogleEndpoint endpoint)
    {
        var client = Client(endpoint);
        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "read a.cs")]);

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "read a.cs"),
            first.Messages[0],
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read_file", "contents")]),
        ]);

        return SignaturesSentOn(endpoint.Bodies[1]);
    }

    private static IReadOnlyList<string> SignaturesSentOn(string body)
    {
        return
        [
            .. JsonDocument.Parse(body).RootElement.GetProperty("contents").EnumerateArray()
                .SelectMany(content => content.GetProperty("parts").EnumerateArray())
                .Where(part => part.TryGetProperty("functionCall", out _))
                .Where(part => part.TryGetProperty("thoughtSignature", out _))
                .Select(part => part.GetProperty("thoughtSignature").GetString()!),
        ];
    }

    private static string SignedCall(string name, string signature)
    {
        return $$$"""
                  {"candidates":[{"content":{"role":"model","parts":[
                      {"functionCall":{"name":"{{{name}}}","args":{"path":"a.cs"}},"thoughtSignature":"{{{signature}}}"}]},
                    "finishReason":"STOP"}],"modelVersion":"gemini-3.8-flash"}
                  """;
    }

    private static string TextResponse(string text)
    {
        return $$$"""
                  {"candidates":[{"content":{"role":"model","parts":[{"text":"{{{text}}}"}]},"finishReason":"STOP"}],
                   "modelVersion":"gemini-3.8-flash"}
                  """;
    }

    private static GoogleGenerateContentChatClient Client(FakeGoogleEndpoint endpoint)
    {
        return new GoogleGenerateContentChatClient(
            new FakeProviderHostContext(endpoint).Http,
            new GoogleCredentialSource(),
            new ProviderEndpoint(
                "meisterdev/googleVertex",
                "https://generativelanguage.googleapis.com",
                GoogleVertexProviderDriver.ApiKeyAuth,
                "gemini-key"),
            Model);
    }
}
