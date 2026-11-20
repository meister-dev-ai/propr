// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.OpenAiAddIn.Tests;

/// <summary>
///     The token usage a call of this family reports, read from the response of the API through the client and the
///     driver as the host reads it.
/// </summary>
/// <remarks>
///     From GPT-5.6 on, OpenAI bills the input a call writes to the prompt cache at its own rate and reports it as
///     <c>usage.input_tokens_details.cache_write_tokens</c> on the Responses API and as
///     <c>usage.prompt_tokens_details.cache_write_tokens</c> on Chat Completions. The OpenAI library has no property
///     for that field, so
///     these cases send the response through the real library and adapter, where the field is lost when the client
///     does not read it. The usage in the responses is the example of OpenAI's prompt-caching guide: 12,000 input
///     tokens, of which 4,000 are read from the cache and 7,000 are written to it, leaving 1,000 ordinary input
///     tokens.
/// </remarks>
public sealed class OpenAiUsageTests
{
    private const string UsageWithCacheWrites =
        """
        { "input_tokens": 12000,
          "input_tokens_details": { "cached_tokens": 4000, "cache_write_tokens": 7000,
            "cache_write_tokens_details": { "ttl_30m_tokens": 7000, "ttl_12h_tokens": 0 } },
          "output_tokens": 300, "output_tokens_details": { "reasoning_tokens": 200 }, "total_tokens": 12300 }
        """;

    private const string UsageWithoutCacheWrites =
        """
        { "input_tokens": 12000, "input_tokens_details": { "cached_tokens": 4000 },
          "output_tokens": 300, "output_tokens_details": { "reasoning_tokens": 200 }, "total_tokens": 12300 }
        """;

    private const string ChatCompletionsUsageWithCacheWrites =
        """
        { "prompt_tokens": 12000, "completion_tokens": 300, "total_tokens": 12300,
          "prompt_tokens_details": { "cached_tokens": 4000, "cache_write_tokens": 7000 },
          "completion_tokens_details": { "reasoning_tokens": 200 } }
        """;

    private static readonly ProviderModelDescriptor Model = new(
        Guid.NewGuid(),
        "gpt-6.1-terra",
        [ProviderDeclaredProtocolModes.Auto, OpenAiProviderDriver.ResponsesProtocol, OpenAiProviderDriver.ChatCompletionsProtocol]);

    [Fact]
    public async Task CacheWritesOfAResponse_AreReadAsTheirOwnBucket()
    {
        var usage = await ReadUsageAsync(UsageWithCacheWrites);

        Assert.Equal(12000, usage.InputTokens);
        Assert.Equal(4000, usage.CachedInputTokens);
        Assert.Equal(7000, usage.CacheWriteTokens);
        Assert.Equal(1000, usage.NonCachedInputTokens);
        Assert.Equal(300, usage.OutputTokens);
        Assert.Equal(200, usage.ReasoningTokens);
        Assert.True(usage.CountersAreInclusive);
    }

    // A model before GPT-5.6 reports no cache writes, and its whole uncached input is billed at the input rate.
    [Fact]
    public async Task AResponseWithoutCacheWrites_LeavesTheUncachedInputAsOrdinaryInput()
    {
        var usage = await ReadUsageAsync(UsageWithoutCacheWrites);

        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(8000, usage.NonCachedInputTokens);
    }

    [Fact]
    public async Task CacheWritesOfAStreamedResponse_AreReadFromItsFinalEvent()
    {
        var driver = new OpenAiProviderDriver();
        using var chat = driver.CreateChatClient(
            EndpointOn(new Endpoint(StreamOf(UsageWithCacheWrites), "text/event-stream")), Model, ProviderDeclaredProtocolModes.Auto);

        UsageDetails? reported = null;
        await foreach (var update in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")]))
        {
            reported = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? reported;
        }

        var usage = driver.ReadUsage(reported);

        Assert.Equal(7000, usage.CacheWriteTokens);
        Assert.Equal(1000, usage.NonCachedInputTokens);
    }

    // OpenAI's API types define the cache-write count of Chat Completions as prompt_tokens_details.cache_write_tokens.
    [Fact]
    public async Task CacheWritesOfAChatCompletionsResponse_AreReadAsTheirOwnBucket()
    {
        var usage = await ReadChatCompletionsUsageAsync(ChatCompletionsUsageWithCacheWrites);

        Assert.Equal(12000, usage.InputTokens);
        Assert.Equal(4000, usage.CachedInputTokens);
        Assert.Equal(7000, usage.CacheWriteTokens);
        Assert.Equal(1000, usage.NonCachedInputTokens);
        Assert.Equal(300, usage.OutputTokens);
        Assert.Equal(200, usage.ReasoningTokens);
        Assert.True(usage.CountersAreInclusive);
    }

    // The last chunk of a streamed Chat Completions call carries the usage and no choices.
    [Fact]
    public async Task CacheWritesOfAStreamedChatCompletionsResponse_AreReadFromItsLastChunk()
    {
        var driver = new OpenAiProviderDriver();
        using var chat = driver.CreateChatClient(
            EndpointOn(new Endpoint(ChunksOf(ChatCompletionsUsageWithCacheWrites), "text/event-stream")), Model, OpenAiProviderDriver.ChatCompletionsProtocol);

        UsageDetails? reported = null;
        await foreach (var update in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")]))
        {
            reported = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? reported;
        }

        var usage = driver.ReadUsage(reported);

        Assert.Equal(7000, usage.CacheWriteTokens);
        Assert.Equal(1000, usage.NonCachedInputTokens);
    }

    // A Chat Completions response without the field reads the counts as before.
    [Fact]
    public async Task AChatCompletionsResponseWithoutCacheWrites_LeavesTheUncachedInputAsOrdinaryInput()
    {
        const string usageWithoutCacheWrites =
            """
            { "prompt_tokens": 12000, "completion_tokens": 300, "total_tokens": 12300,
              "prompt_tokens_details": { "cached_tokens": 4000 } }
            """;

        var usage = await ReadChatCompletionsUsageAsync(usageWithoutCacheWrites);

        Assert.Equal(4000, usage.CachedInputTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(8000, usage.NonCachedInputTokens);
    }

    private static async Task<ProviderTokenUsage> ReadUsageAsync(string usage)
    {
        var driver = new OpenAiProviderDriver();
        using var chat = driver.CreateChatClient(EndpointOn(new Endpoint(ResponseWith(usage), "application/json")), Model, ProviderDeclaredProtocolModes.Auto);

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")]);

        return driver.ReadUsage(response.Usage);
    }

    private static async Task<ProviderTokenUsage> ReadChatCompletionsUsageAsync(string usage)
    {
        var driver = new OpenAiProviderDriver();
        using var chat = driver.CreateChatClient(
            EndpointOn(new Endpoint(ChatCompletionWith(usage), "application/json")), Model, OpenAiProviderDriver.ChatCompletionsProtocol);

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")]);

        return driver.ReadUsage(response.Usage);
    }

    private static string ChatCompletionWith(string usage)
    {
        return
            $$"""
              {
                "id": "chatcmpl-fake", "object": "chat.completion", "created": 1770000000, "model": "gpt-6.1-terra",
                "choices": [{ "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "hi" } }],
                "usage": {{usage}}
              }
              """;
    }

    // The chunks of a streamed Chat Completions call: one with the answer, and a last one with the usage and no
    // choices.
    private static string ChunksOf(string usage)
    {
        static string OneLine(string json)
        {
            return string.Join(' ', json.Split('\n', StringSplitOptions.TrimEntries));
        }

        const string head = "\"id\": \"chatcmpl-fake\", \"object\": \"chat.completion.chunk\", \"created\": 1770000000, \"model\": \"gpt-6.1-terra\"";
        const string answer = "\"choices\": [{ \"index\": 0, \"delta\": { \"role\": \"assistant\", \"content\": \"hi\" }, \"finish_reason\": \"stop\" }]";

        return $"data: {{ {head}, {answer} }}\n\n"
               + $"data: {{ {head}, \"choices\": [], \"usage\": {OneLine(usage)} }}\n\n"
               + "data: [DONE]\n\n";
    }

    private static string ResponseWith(string usage)
    {
        return
            $$"""
              {
                "id": "resp_fake", "object": "response", "created_at": 1770000000, "model": "gpt-6.1-terra",
                "status": "completed",
                "output": [{
                  "type": "message", "id": "msg_fake", "status": "completed", "role": "assistant",
                  "content": [{ "type": "output_text", "text": "hi", "annotations": [] }]
                }],
                "parallel_tool_calls": false, "tool_choice": "auto", "tools": [],
                "usage": {{usage}}
              }
              """;
    }

    // The events of a streamed response that the usage is read from: the response opens and completes, and the
    // completed event carries the response with its usage.
    private static string StreamOf(string usage)
    {
        static string OneLine(string json)
        {
            return string.Join(' ', json.Split('\n', StringSplitOptions.TrimEntries));
        }

        const string opened =
            """
            { "type": "response.created", "sequence_number": 0, "response": {
                "id": "resp_fake", "object": "response", "created_at": 1770000000, "model": "gpt-6.1-terra",
                "status": "in_progress", "output": [], "parallel_tool_calls": false, "tool_choice": "auto", "tools": [] } }
            """;
        var completed = $$"""{ "type": "response.completed", "sequence_number": 1, "response": {{ResponseWith(usage)}} }""";

        return $"event: response.created\ndata: {OneLine(opened)}\n\n"
               + $"event: response.completed\ndata: {OneLine(completed)}\n\n";
    }

    private static ProviderEndpoint EndpointOn(HttpMessageHandler wire)
    {
        return new ProviderEndpoint("meisterdev/openAi", "https://api.openai.com/v1", OpenAiProviderDriver.ApiKeyAuth, "sk-test-key")
        {
            HostContext = new FakeProviderHostContext(wire),
        };
    }

    /// <summary>Answers every request with the same body.</summary>
    private sealed class Endpoint(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, mediaType),
                });
        }
    }
}
