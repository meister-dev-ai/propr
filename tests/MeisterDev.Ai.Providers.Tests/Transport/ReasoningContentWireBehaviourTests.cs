// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using MeisterDev.Ai.Providers.Tests.Drivers;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAiChatClient = OpenAI.Chat.ChatClient;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     Pins what actually crosses the wire for a DeepSeek-style model, using a fake compatible endpoint rather
///     than assumptions. These are the facts that decided where the round-trip had to live: the client library
///     surfaces the non-standard <c>reasoning_content</c> field inbound but drops it outbound, which is why
///     <see cref="ReasoningContentRoundTripHandler" /> works below the library rather than above it.
/// </summary>
/// <remarks>
///     The client library is driven directly here rather than through a provider family. The handler is the
///     host's, and it sits on the pipeline every OpenAI-shaped family goes out on, so what it has to repair is
///     the library's own serialization and not any one family's construction of a client.
/// </remarks>
public sealed class ReasoningContentWireBehaviourTests
{
    [Fact]
    public async Task TheAnswerItselfSurvivesTheRoundTrip()
    {
        var endpoint = new FakeCompatibleEndpoint().RespondsWithReasoning("42", "let me think");

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "what is 6*7?")]);

        Assert.Equal("42", response.Text);
        Assert.Single(endpoint.RequestBodies);
    }

    // The client library does surface the non-standard field, as reasoning content on the assistant message. That
    // is what makes the quirk addressable above the transport at all, so it is pinned here.
    [Fact]
    public async Task ReasoningContentArrivesAsReasoningContentOnTheAssistantMessage()
    {
        var endpoint = new FakeCompatibleEndpoint().RespondsWithReasoning("42", "let me think");

        var response = await Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "what is 6*7?")]);

        var reasoning = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<TextReasoningContent>()
            .Select(part => part.Text)
            .ToList();

        Assert.Contains("let me think", reasoning);
    }

    // The other direction, and the constraint that decided the design: reasoning content held on an assistant
    // turn is NOT serialized back, because the field is not part of the OpenAI request schema the client library
    // writes. A model that requires its chain of thought echoed cannot be satisfied from here — only from below,
    // which the transport handler exists for (see the last test).
    [Fact]
    public async Task ReasoningContentOnAnAssistantTurn_IsNotSentBack()
    {
        var endpoint = new FakeCompatibleEndpoint().RespondsWithReasoning("second", "more thought");

        var priorTurn = new ChatMessage(
            ChatRole.Assistant, [
                new TextReasoningContent("earlier thought"),
                new TextContent("first"),
            ]);

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "one"),
            priorTurn,
            new ChatMessage(ChatRole.User, "two"),
        ]);

        var body = Assert.Single(endpoint.RequestBodies);
        Assert.DoesNotContain("earlier thought", body, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_content", body, StringComparison.Ordinal);
        // The answer text still travels, so only the reasoning is lost — which is precisely the case that fails.
        Assert.Contains("\"first\"", body, StringComparison.Ordinal);
    }

    // Nor does the other candidate channel work: additional properties on a message are not serialized either.
    [Fact]
    public async Task AdditionalPropertiesOnAnAssistantTurn_AreNotSentBackEither()
    {
        var endpoint = new FakeCompatibleEndpoint().RespondsWithReasoning("second", "more thought");

        var priorAssistantTurn = new ChatMessage(ChatRole.Assistant, "first")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoning_content"] = "earlier thought" },
        };

        await Client(endpoint).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "one"),
            priorAssistantTurn,
            new ChatMessage(ChatRole.User, "two"),
        ]);

        var body = Assert.Single(endpoint.RequestBodies);

        // The finding: additional properties on a ChatMessage are not serialized into the OpenAI request shape,
        // so a decorator above the transport cannot put this field back. Pinned so the constraint is visible.
        Assert.DoesNotContain("earlier thought", body, StringComparison.Ordinal);
        Assert.Contains("\"first\"", body, StringComparison.Ordinal);
    }

    // The resolution, end to end through the real client library: with the transport handler in the pipeline, a
    // second call carries the field the provider demands, even though nothing above the wire can express it.
    [Fact]
    public async Task WithTheTransportHandler_TheSecondCallCarriesTheReasoningBack()
    {
        var endpoint = new FakeCompatibleEndpoint()
            .RespondsWithReasoning("42", "let me think")
            .RespondsWithReasoning("still 42", "more thought");
        var client = Client(endpoint, withRoundTrip: true);

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "what is 6*7?")]);
        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "what is 6*7?"),
            new ChatMessage(ChatRole.Assistant, first.Text),
            new ChatMessage(ChatRole.User, "are you sure?"),
        ]);

        var secondRequest = endpoint.RequestBodies[1];
        Assert.Contains("reasoning_content", secondRequest, StringComparison.Ordinal);
        Assert.Contains("let me think", secondRequest, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A chat client of the library the OpenAI-shaped families use, sending over the host's runtime pipeline
    ///     with or without the round-trip on it.
    /// </summary>
    /// <param name="endpoint">What the pipeline sends to.</param>
    /// <param name="withRoundTrip">Whether the reasoning round-trip sits between the library and the endpoint.</param>
    private static IChatClient Client(FakeCompatibleEndpoint endpoint, bool withRoundTrip = false)
    {
        HttpMessageHandler pipeline = withRoundTrip
            ? new ReasoningContentRoundTripHandler { InnerHandler = endpoint }
            : endpoint;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://api.openai.com/v1", UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(new HttpClient(pipeline)),
        };

        return new OpenAiChatClient("deepseek-reasoner", new ApiKeyCredential("key"), options).AsIChatClient();
    }
}
