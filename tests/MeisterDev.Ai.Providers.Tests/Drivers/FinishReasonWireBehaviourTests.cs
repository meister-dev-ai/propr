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
///     Pins what the client library does with a finish reason it cannot read, using a fake compatible endpoint
///     rather than assumptions. These are the facts that decided where the repair had to live: the failure happens
///     inside the library's deserializer, before any response object exists, which is why
///     <see cref="FinishReasonNormalizingHandler" /> works below the library rather than above it.
/// </summary>
public sealed class FinishReasonWireBehaviourTests
{
    private static readonly ProviderEndpoint Endpoint =
        new(AiProviderKind.OpenAiCompatible, "https://opencode.ai/zen/v1", AiAuthMode.ApiKey, "key");

    private static readonly ProviderModelDescriptor Model =
        new(Guid.NewGuid(), "gpt-5.6-luna", [AiProtocolMode.ChatCompletions], null);

    // The defect itself: a null finish reason is not tolerated, and the call dies before the caller sees the
    // message. Pinned so the reason the handler exists stays visible if the library ever opens the enum up.
    [Fact]
    public async Task WithoutTheHandler_ANullFinishReasonFailsTheCall()
    {
        var endpoint = new FakeCompatibleEndpoint().Responds(ToolCallCompletion(finishReason: null));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]));
    }

    // A vendor term forwarded untranslated fails the same way, which is why the repair is not null-specific.
    [Fact]
    public async Task WithoutTheHandler_AForeignFinishReasonFailsTheCall()
    {
        var endpoint = new FakeCompatibleEndpoint().Responds(ToolCallCompletion("tool_use"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client(endpoint).GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]));
    }

    // The resolution, end to end through the real client library.
    [Fact]
    public async Task WithTheHandler_ANullFinishReasonIsReadAsToolCalls()
    {
        var endpoint = new FakeCompatibleEndpoint().Responds(ToolCallCompletion(finishReason: null));

        var response = await Client(endpoint, withRepair: true)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    // The point of the whole exercise: the tool call the model actually made has to survive the repair, because
    // an agentic review that cannot see its own tool calls is no better off than one that threw.
    [Fact]
    public async Task WithTheHandler_TheToolCallItselfSurvives()
    {
        var endpoint = new FakeCompatibleEndpoint().Responds(ToolCallCompletion(finishReason: null));

        var response = await Client(endpoint, withRepair: true)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("read_file", call.Name);
    }

    // A turn with no tool calls must not be reported as one; it stopped because it was finished.
    [Fact]
    public async Task WithTheHandler_ANullFinishReasonOnAPlainAnswerIsReadAsStop()
    {
        var endpoint = new FakeCompatibleEndpoint().Responds(PlainCompletion(finishReason: null));

        var response = await Client(endpoint, withRepair: true)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("looks fine", response.Text);
    }

    // A conforming provider has to come through untouched, since the handler sits on every OpenAI-shaped call.
    [Fact]
    public async Task WithTheHandler_AConformingCompletionIsUnchanged()
    {
        var endpoint = new FakeCompatibleEndpoint().Responds(PlainCompletion("length"));

        var response = await Client(endpoint, withRepair: true)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        Assert.Equal("looks fine", response.Text);
    }

    private static string ToolCallCompletion(string? finishReason)
    {
        return Completion(
            finishReason,
            """
            "content": null,
            "tool_calls": [
              {"id": "call_1", "type": "function", "function": {"name": "read_file", "arguments": "{\"path\":\"a.cs\"}"}}
            ]
            """);
    }

    private static string PlainCompletion(string? finishReason)
    {
        return Completion(finishReason, "\"content\": \"looks fine\"");
    }

    private static string Completion(string? finishReason, string messageFields)
    {
        var finishJson = finishReason is null ? "null" : $"\"{finishReason}\"";
        return $$"""
                 {
                   "id": "chatcmpl-fake",
                   "object": "chat.completion",
                   "created": 1770000000,
                   "model": "gpt-5.6-luna",
                   "choices": [
                     {
                       "index": 0,
                       "finish_reason": {{finishJson}},
                       "message": { "role": "assistant", {{messageFields}} }
                     }
                   ],
                   "usage": { "prompt_tokens": 11, "completion_tokens": 7, "total_tokens": 18 }
                 }
                 """;
    }

    private static IChatClient Client(FakeCompatibleEndpoint endpoint, bool withRepair = false)
    {
        var services = new ServiceCollection();
        var runtime = services.AddHttpClient("AiProviderRuntime");
        if (withRepair)
        {
            runtime.AddHttpMessageHandler(() => new FinishReasonNormalizingHandler());
        }

        runtime.ConfigurePrimaryHttpMessageHandler(() => endpoint);
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
