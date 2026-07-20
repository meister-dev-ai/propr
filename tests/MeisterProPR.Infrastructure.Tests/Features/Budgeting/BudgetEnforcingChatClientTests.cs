// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

public sealed class BudgetEnforcingChatClientTests
{
    // $2 per one million input tokens; a one-million-input-token response therefore costs exactly $2.
    private static readonly ModelPricing Pricing = new(InputCostPer1MUsd: 2m, OutputCostPer1MUsd: 4m);

    [Fact]
    public async Task GetResponseAsync_MetersTheCallCost_WhenAScopeIsActive()
    {
        var stub = new StubChatClient(ResponseWith(inputTokens: 1_000_000));
        var accessor = new BudgetScopeAccessor();
        var client = new BudgetEnforcingChatClient(stub, accessor, Pricing);
        var scope = ScopeWithIncrementHardCap(capUsd: 100m, baselineUsd: 0m);

        using (accessor.BeginScope(scope))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }

        Assert.Equal(1, stub.CallCount);
        Assert.Equal(2m, scope.RunningUsd);
    }

    [Fact]
    public async Task GetResponseAsync_StopsBeforeCalling_WhenTheHardCapIsAlreadyReached()
    {
        var stub = new StubChatClient(ResponseWith(inputTokens: 0));
        var accessor = new BudgetScopeAccessor();
        var client = new BudgetEnforcingChatClient(stub, accessor, Pricing);
        var scope = ScopeWithIncrementHardCap(capUsd: 5m, baselineUsd: 5m);

        using (accessor.BeginScope(scope))
        {
            await Assert.ThrowsAsync<BudgetHardCapReachedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        }

        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_IsATransparentPassThrough_WhenNoScopeIsActive()
    {
        var stub = new StubChatClient(ResponseWith(inputTokens: 1_000_000));
        var client = new BudgetEnforcingChatClient(stub, new BudgetScopeAccessor(), Pricing);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(1, stub.CallCount);
    }

    private static BudgetScope ScopeWithIncrementHardCap(decimal capUsd, decimal baselineUsd)
    {
        var caps = new BudgetCaps(null, null, null, null, null, capUsd);
        var baseline = new ReviewSpendBaseline(
            ReviewScopeSpend.None,
            ReviewScopeSpend.None,
            new ReviewScopeSpend(baselineUsd, false));
        return new BudgetScope(caps, baseline);
    }

    private static ChatResponse ResponseWith(long inputTokens)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = 0 },
        };
    }

    private sealed class StubChatClient(ChatResponse response) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(response);
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
