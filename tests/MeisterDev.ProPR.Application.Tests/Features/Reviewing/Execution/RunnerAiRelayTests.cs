// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerAiRelayTests
{
    private static readonly Guid JobId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly RunnerCallContext Call = new(JobId, 2, "runner-a");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IRunnerJobBudgetRegistry _budgets = new RunnerJobBudgetRegistry();
    private readonly IChatClient _client = Substitute.For<IChatClient>();
    private readonly IRunnerRelayModelResolver _models = Substitute.For<IRunnerRelayModelResolver>();
    private readonly RunnerRelayReplayCache _replays = new();
    private readonly IRunnerRelayUsageRecorder _usage = Substitute.For<IRunnerRelayUsageRecorder>();

    private static RunnerRelayRequest Request(string key = "call-1")
    {
        return new RunnerRelayRequest(
            "reviewer-medium",
            [new ChatMessage(ChatRole.User, "review this")],
            null,
            key);
    }

    private static BudgetScope Budget(decimal? hardCap, decimal alreadySpent, decimal? incrementSoftCap = null)
    {
        return new BudgetScope(
            new BudgetCaps(null, hardCap, null, null, incrementSoftCap, null),
            new ReviewSpendBaseline(
                new ReviewScopeSpend(alreadySpent, false),
                ReviewScopeSpend.None,
                new ReviewScopeSpend(alreadySpent, false)));
    }

    private RunnerAiRelay CreateRelay(bool authorized = true, BudgetScope? budget = null, decimal? costPerCall = 1m)
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                authorized
                    ? RunnerCallAuthorization.Allow(Guid.NewGuid())
                    : RunnerCallAuthorization.Refuse(RunnerCallRefusal.NotTheLeaseHolder));

        // The cost per call comes out of the pricing, not out of the recorder: one million output tokens
        // at a per-million rate equal to the wanted cost. A null rate is a model with no pricing at all.
        this._models.ResolveAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RunnerRelayModel(this._client, AiProviderKind.OpenAi, new ModelPricing(null, costPerCall)));
        this._client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    Usage = new UsageDetails { OutputTokenCount = 1_000_000 },
                });

        if (budget is not null)
        {
            this._budgets.Register(JobId, budget);
        }

        return new RunnerAiRelay(this._authorizer, this._budgets, this._models, this._usage, this._replays);
    }

    [Fact]
    public async Task AnAuthorizedCall_IsCompletedAndItsUsageRecordedOnce()
    {
        var relay = this.CreateRelay(budget: Budget(hardCap: null, alreadySpent: 0m));

        var result = await relay.CompleteAsync(Call, Request());

        Assert.True(result.IsCompleted);
        await this._usage.Received(1).RecordAsync(JobId, "reviewer-medium", "call-1", Arg.Any<UsageDetails?>(), Arg.Any<CancellationToken>());
    }

    // The key never leaves the control plane: the executor names a model and the relay resolves it here.
    [Fact]
    public async Task TheModelIsResolvedByName_AgainstTheJobsOwnClient()
    {
        var clientId = Guid.NewGuid();
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Allow(clientId));
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Allow(clientId));

        await relay.CompleteAsync(Call, Request());

        await this._models.Received().ResolveAsync(clientId, "reviewer-medium", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnauthorizedCall_NeverReachesAProvider()
    {
        var relay = this.CreateRelay(authorized: false, budget: Budget(null, 0m));

        var result = await relay.CompleteAsync(Call, Request());

        Assert.Equal(RunnerRelayRefusal.NotAuthorized, result.Refusal);
        Assert.Equal(RunnerCallRefusal.NotTheLeaseHolder, result.CallRefusal);
        await this._client.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // Refusing to spend is the only enforcement that works. Noticing afterwards means the money is gone.
    [Fact]
    public async Task OnceTheHardCapIsReached_FurtherCompletionsAreRefusedBeforeTheProviderIsCalled()
    {
        var relay = this.CreateRelay(budget: Budget(hardCap: 10m, alreadySpent: 10m));

        var result = await relay.CompleteAsync(Call, Request());

        Assert.Equal(RunnerRelayRefusal.BudgetHardCapReached, result.Refusal);
        Assert.NotNull(result.Breach);
        await this._client.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SpendAccumulates_UntilTheCapTrips()
    {
        var relay = this.CreateRelay(budget: Budget(hardCap: 2m, alreadySpent: 0m), costPerCall: 1m);

        Assert.True((await relay.CompleteAsync(Call, Request("a"))).IsCompleted);
        Assert.True((await relay.CompleteAsync(Call, Request("b"))).IsCompleted);
        var third = await relay.CompleteAsync(Call, Request("c"));

        Assert.Equal(RunnerRelayRefusal.BudgetHardCapReached, third.Refusal);
    }

    // A model with no configured rates prices to null, and null is "unpriced", never zero — the total
    // stays where it was and only the approximate flag moves. The cap cannot trip on unknowable spend.
    [Fact]
    public async Task AnUnpricedModel_DoesNotAccrueTowardTheCap()
    {
        var relay = this.CreateRelay(budget: Budget(hardCap: 2m, alreadySpent: 0m), costPerCall: null);

        Assert.True((await relay.CompleteAsync(Call, Request("a"))).IsCompleted);
        Assert.True((await relay.CompleteAsync(Call, Request("b"))).IsCompleted);
        Assert.True((await relay.CompleteAsync(Call, Request("c"))).IsCompleted);
    }

    // The relay is scoped to one HTTP request, and a retry after a network failure arrives on a different
    // one by definition. The replay cache is shared state precisely so that the retry finds the answer.
    [Fact]
    public async Task ARetryServedByADifferentRelayInstance_IsReplayedNotRecharged()
    {
        var first = await this.CreateRelay(budget: Budget(hardCap: 1.5m, alreadySpent: 0m))
            .CompleteAsync(Call, Request("same-key"));
        var retry = await this.CreateRelay()
            .CompleteAsync(Call, Request("same-key"));

        Assert.True(first.IsCompleted);
        Assert.True(retry.Replayed);
        await this._client.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        // The budget saw exactly one charge; a second would have tripped the cap on spend that never happened.
        Assert.True((await this.CreateRelay().CompleteAsync(Call, Request("fresh-key"))).IsCompleted);
    }

    // A retry that reached the provider once has to cost what it actually cost. Charging again would trip
    // the cap on spend that never happened.
    [Fact]
    public async Task ARetryCarryingTheSameKey_IsAnsweredWithoutSpendingAgain()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));

        var first = await relay.CompleteAsync(Call, Request("same-key"));
        var retry = await relay.CompleteAsync(Call, Request("same-key"));

        Assert.True(first.IsCompleted);
        Assert.True(retry.IsCompleted);
        Assert.True(retry.Replayed);
        Assert.False(first.Replayed);
        await this._client.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await this._usage.Received(1).RecordAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<UsageDetails?>(),
            Arg.Any<CancellationToken>());
    }

    // The soft cap means wind down to a synthesis, not stop, and synthesis still needs completions.
    [Fact]
    public async Task TheSoftCap_IsReportedRatherThanEnforced()
    {
        var relay = this.CreateRelay(budget: Budget(hardCap: null, alreadySpent: 5m, incrementSoftCap: 1m));

        var result = await relay.CompleteAsync(Call, Request());

        Assert.True(result.IsCompleted);
        Assert.True(result.SoftCapReached);
    }

    // Without the job's budget there is nothing to charge, and an uncharged completion is how a job spends
    // past its cap.
    [Fact]
    public async Task WithoutTheJobsBudget_TheCompletionIsRefused()
    {
        var relay = this.CreateRelay();

        var result = await relay.CompleteAsync(Call, Request());

        Assert.Equal(RunnerRelayRefusal.JobNotHeld, result.Refusal);
    }

    // Review passes use tool calling; a relay that dropped the options would quietly change the review.
    [Fact]
    public async Task ChatOptionsIncludingTools_ReachTheProviderUnchanged()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", "probe")] };

        await relay.CompleteAsync(
            Call,
            new RunnerRelayRequest("reviewer-medium", [new ChatMessage(ChatRole.User, "hi")], options, "k"));

        await this._client.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.Tools != null && o.Tools.Count == 1),
            Arg.Any<CancellationToken>());
    }
}
