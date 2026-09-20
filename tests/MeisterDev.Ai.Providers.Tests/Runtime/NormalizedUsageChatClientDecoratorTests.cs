// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Runtime;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Runtime;

/// <summary>
///     The stage that puts a family's mapped counters onto the response, so everything downstream of the provider
///     layer reads the host's counter names instead of a vendor's.
/// </summary>
/// <remarks>
///     The readers are the reason this exists rather than each of them calling the driver: the budget stage, the
///     telemetry stage, the review loop's protocol and the relay's serialised response all take a response and
///     none of them holds a driver. A runner holds no driver at all.
/// </remarks>
public sealed class NormalizedUsageChatClientDecoratorTests
{
    // A vendor that reports its input count exclusive of the cache buckets: read as it stands, the 120 real
    // prompt tokens price at nothing, because the host bills the input total less the buckets floored at zero.
    [Fact]
    public async Task AVendorsExclusiveInputCountLeavesTheStageInclusiveOfItsCacheBuckets()
    {
        var inner = new AnsweringChatClient(
            new UsageDetails
            {
                InputTokenCount = 120,
                OutputTokenCount = 207,
                CachedInputTokenCount = 4000,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["a_vendors_own_name"] = 50 },
            });

        using var client = Decorate(new AddsBothBucketsBack("a_vendors_own_name"), inner);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var usage = ProviderTokenUsage.FromUsageDetails(response.Usage);

        Assert.Equal(4170, usage.InputTokens);
        Assert.Equal(4000, usage.CachedInputTokens);
        Assert.Equal(50, usage.CacheWriteTokens);
        Assert.Equal(120, usage.NonCachedInputTokens);
        Assert.True(usage.CountersAreInclusive);
    }

    // Most readers of a response hold no driver, so the counters have to be readable without one.
    [Fact]
    public async Task TheMappedCountersAreReadableWithoutTheDriverThatProducedThem()
    {
        var inner = new AnsweringChatClient(
            new UsageDetails
            {
                InputTokenCount = 120,
                OutputTokenCount = 207,
                CachedInputTokenCount = 4000,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["a_vendors_own_name"] = 50 },
            });

        using var client = Decorate(new AddsBothBucketsBack("a_vendors_own_name"), inner);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(50, ProviderTokenUsage.FromUsageDetails(response.Usage).CacheWriteTokens);
        Assert.False(response.Usage!.AdditionalCounts?.ContainsKey("a_vendors_own_name"));
    }

    // A call that reported nothing has to stay distinguishable from one that measured zero, or an unreported call
    // is recorded as a free one.
    [Fact]
    public async Task ACallThatReportedNoUsageIsNotGivenOne()
    {
        using var client = Decorate(new AddsBothBucketsBack("a_vendors_own_name"), new AnsweringChatClient(usage: null));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Null(response.Usage);
        Assert.True(ProviderTokenUsage.FromUsageDetails(response.Usage).IsEstimated);
    }

    private static IChatClient Decorate(IAiProviderDriver driver, IChatClient inner)
    {
        return new NormalizedUsageChatClientDecorator(driver).Decorate(inner, Endpoint(), Model());
    }

    private static ProviderEndpoint Endpoint()
    {
        return new ProviderEndpoint(
            "meisterdev/anthropic",
            "https://api.anthropic.com/v1",
            "meisterdev/anthropic:ApiKey",
            "a-key");
    }

    private static ProviderModelDescriptor Model()
    {
        return new ProviderModelDescriptor(
            Guid.NewGuid(),
            "a-model",
            [ProviderDeclaredProtocolModes.Auto]);
    }

    /// <summary>A family whose vendor reports input exclusive of its buckets and names the write bucket itself.</summary>
    private sealed class AddsBothBucketsBack(string cacheWriteName) : StubDriver
    {
        public override ProviderTokenUsage ReadUsage(UsageDetails? usage)
        {
            if (usage is null)
            {
                return ProviderTokenUsage.Missing;
            }

            var cacheRead = usage.CachedInputTokenCount ?? 0;
            var cacheWrite = usage.AdditionalCounts?.TryGetValue(cacheWriteName, out var written) == true ? written : 0;

            return new ProviderTokenUsage(
                (usage.InputTokenCount ?? 0) + cacheRead + cacheWrite,
                usage.OutputTokenCount ?? 0,
                cacheRead,
                cacheWrite);
        }
    }

    private sealed class AnsweringChatClient(UsageDetails? usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
