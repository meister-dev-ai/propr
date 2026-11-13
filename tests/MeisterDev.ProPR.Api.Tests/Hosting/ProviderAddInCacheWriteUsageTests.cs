// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Runtime;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Domain.Services;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Api.Tests.Hosting;

/// <summary>
///     The cache writes a call through the bundled OpenAI add-in reports, as the host records and prices them.
/// </summary>
/// <remarks>
///     From GPT-5.6 on, OpenAI bills the input a call writes to the prompt cache at its own rate. The add-in loads its
///     own copy of the OpenAI library in a load context of its own, and that library has no property for the field,
///     so the count reaches the host only when the add-in reads it from the response. The usage is the example of
///     OpenAI's prompt-caching guide, and the prices are those OpenAI lists for gpt-6.1-terra.
/// </remarks>
public sealed class ProviderAddInCacheWriteUsageTests
{
    private const string ResponsesProtocol = "meisterdev/openAi:Responses";

    private const string ResponseBody =
        """
        {
          "id": "resp_fake", "object": "response", "created_at": 1770000000, "model": "gpt-6.1-terra",
          "status": "completed",
          "output": [{
            "type": "message", "id": "msg_fake", "status": "completed", "role": "assistant",
            "content": [{ "type": "output_text", "text": "hi", "annotations": [] }]
          }],
          "parallel_tool_calls": false, "tool_choice": "auto", "tools": [],
          "usage": { "input_tokens": 12000,
            "input_tokens_details": { "cached_tokens": 4000, "cache_write_tokens": 7000,
              "cache_write_tokens_details": { "ttl_30m_tokens": 7000, "ttl_12h_tokens": 0 } },
            "output_tokens": 300, "output_tokens_details": { "reasoning_tokens": 200 }, "total_tokens": 12300 }
        }
        """;

    [Fact]
    public async Task CacheWritesOfTheBundledOpenAiAddIn_AreRecordedAndPricedAtTheCacheWriteRate()
    {
        var driver = LoadBundledOpenAiDriver();
        var endpoint = new ProviderEndpoint("meisterdev/openAi", "https://api.openai.example/v1", "meisterdev/openAi:ApiKey", "sk-test")
        {
            HostContext = new TransportOnlyHostContext(new CompletedResponse()),
        };
        var model = new ProviderModelDescriptor(Guid.NewGuid(), "gpt-6.1-terra", [ResponsesProtocol]);
        using var chat = new NormalizedUsageChatClientDecorator(driver)
            .Decorate(driver.CreateChatClient(endpoint, model, ResponsesProtocol), endpoint, model);

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")]);
        var usage = AiTokenUsageExtractor.FromResponse(response);
        var cost = AiCostCalculator.Calculate(usage, new ModelPricing(1.00m, 5.00m, 0.05m, 1.25m));

        Assert.Equal(4000, usage.CachedInputTokens);
        Assert.Equal(7000, usage.CacheWriteTokens);
        Assert.Equal(1000, usage.NonCachedInputTokens);

        // 1,000 × $1.00 + 4,000 × $0.05 + 7,000 × $1.25 + 300 × $5.00, per million tokens.
        Assert.Equal(0.01145m, cost.Usd);
        Assert.False(cost.IsApproximate);
    }

    // Loads the bundled OpenAI add-in from the directory the build copies it to, in a load context of its own.
    private static IAiProviderDriver LoadBundledOpenAiDriver()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "provider-add-ins",
            "MeisterDev.Ai.Providers.OpenAiAddIn",
            "MeisterDev.Ai.Providers.OpenAiAddIn.dll");
        var assembly = new ProviderAddInLoadContext(path).LoadFromAssemblyPath(path);
        var driverType = assembly.GetTypes()
            .Single(type => typeof(IAiProviderDriver).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false });

        return (IAiProviderDriver)Activator.CreateInstance(driverType)!;
    }

    /// <summary>Answers every request with the completed response.</summary>
    private sealed class CompletedResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
                });
        }
    }
}
