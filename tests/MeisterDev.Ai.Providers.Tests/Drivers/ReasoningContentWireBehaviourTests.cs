// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Pins what actually crosses the wire for a DeepSeek-style model, using a fake compatible endpoint rather
///     than assumptions. These are the facts a reasoning-content normalizer has to be built against: whether the
///     non-standard <c>reasoning_content</c> field survives the client library in either direction.
/// </summary>
public sealed class ReasoningContentWireBehaviourTests
{
    private static readonly ProviderEndpoint Endpoint =
        new(AiProviderKind.OpenAiCompatible, "https://api.deepseek.com/v1", AiAuthMode.ApiKey, "key");

    private static readonly ProviderModelDescriptor Model =
        new(Guid.NewGuid(), "deepseek-reasoner", [AiProtocolMode.ChatCompletions], "reasoning_content");

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

    // The other direction, and the constraint that decides the design: reasoning content held on an assistant
    // turn is NOT serialized back, because the field is not part of the OpenAI request schema the client library
    // writes. A model that requires its chain of thought echoed therefore cannot be satisfied from here.
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

    private static IChatClient Client(FakeCompatibleEndpoint endpoint)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("AiProviderRuntime").ConfigurePrimaryHttpMessageHandler(() => endpoint);
        services.AddHttpClient("AiProviderAdmin").ConfigurePrimaryHttpMessageHandler(() => endpoint);
        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        var provider = services.BuildServiceProvider();

        var driver = new OpenAiCompatibleProviderDriver(
            provider.GetRequiredService<OpenAiCompatibleTransport>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            allowPrivateEgress: false,
            allowInsecureScheme: false);

        return driver.CreateChatClient(Endpoint, Model, AiProtocolMode.ChatCompletions);
    }
}
