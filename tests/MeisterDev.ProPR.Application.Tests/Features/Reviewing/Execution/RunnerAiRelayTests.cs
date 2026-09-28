// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.Ai.Providers.Usage;
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

    private readonly IRunnerJobReasoningCapturePolicy _reasoningCapture =
        Substitute.For<IRunnerJobReasoningCapturePolicy>();

    // The executor resolves no provider driver, so what it meters is what comes back here. A relay that dropped
    // a bucket would leave a remote review priced differently from the same review run in process.
    [Fact]
    public async Task ACompletedCallCarriesTheCountersTheDriverProduced()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.Answers(new ProviderTokenUsage(4170, 207, 4000, 50, 200).ToUsageDetails());

        var result = await relay.CompleteAsync(Call, Request());

        Assert.Equal(new ProviderTokenUsage(4170, 207, 4000, 50, 200), result.Usage);
    }

    // A retry answered from the first attempt is charged nothing further, and has to report the same counters:
    // an executor that recorded zeros for a replay would under-report the spend the first attempt made.
    [Fact]
    public async Task AReplayedCallCarriesTheSameCountersAsTheAttemptItReplays()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.Answers(new ProviderTokenUsage(4170, 207, 4000, 50, 200).ToUsageDetails());

        var first = await relay.CompleteAsync(Call, Request());
        var replayed = await relay.CompleteAsync(Call, Request());

        Assert.True(replayed.Replayed);
        Assert.Equal(first.Usage, replayed.Usage);
    }

    // A call the provider reported no usage for stays distinguishable from one that measured zero, or the
    // executor records an unmetered call as a free one.
    [Fact]
    public async Task ACallThatReportedNoUsageCarriesZeroedCountersFlaggedAsEstimated()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.Answers(usage: null);

        var result = await relay.CompleteAsync(Call, Request());

        Assert.Equal(ProviderTokenUsage.Missing, result.Usage);
    }

    /// <summary>Has the resolved client answer with one usage payload, replacing whatever the fixture set.</summary>
    private void Answers(UsageDetails? usage)
    {
        this._client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage });
    }

    private static RunnerRelayRequest Request(string key = "call-1")
    {
        return new RunnerRelayRequest(
            "reviewer-medium",
            [new ChatMessage(ChatRole.User, "review this")],
            _ => null,
            key);
    }

    private static BudgetScope Budget(decimal? hardCap, decimal alreadySpent, decimal? incrementSoftCap = null)
    {
        return new BudgetScope(
            new BudgetCaps(null, hardCap, null, null, incrementSoftCap, null),
            new ReviewSpendBaseline(
                new ReviewScopeSpend(alreadySpent, false),
                ReviewScopeSpend.None,
                new ReviewScopeSpend(alreadySpent, false), ReviewScopeSpend.None));
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
            .Returns(new RunnerRelayModel(this._client, new ModelPricing(null, costPerCall)));
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

        return new RunnerAiRelay(
            this._authorizer,
            this._budgets,
            this._models,
            this._usage,
            this._replays,
            this._reasoningCapture);
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

    // A model with no configured rates prices to null, and null means unpriced rather than zero: the total
    // stays where it was and only the approximate flag moves. The cap cannot trip on unknown spend.
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

    // Review passes use tool calling, so a relay that dropped the options would change the review.
    [Fact]
    public async Task ChatOptionsIncludingTools_ReachTheProviderUnchanged()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", "probe")] };

        await relay.CompleteAsync(
            Call,
            new RunnerRelayRequest("reviewer-medium", [new ChatMessage(ChatRole.User, "hi")], _ => options, "k"));

        await this._client.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.Tools != null && o.Tools.Count == 1),
            Arg.Any<CancellationToken>());
    }

    // A runner holding a valid credential can name any job id it likes. Reading the owning tenant's policy for
    // one it does not hold would let it probe the control plane for other tenants' jobs on every chat request.
    [Fact]
    public async Task ACallThatFailsTheLeaseCheck_ReadsNoReasoningCapturePolicy()
    {
        var relay = this.CreateRelay(authorized: false, budget: Budget(null, 0m));

        var result = await relay.CompleteAsync(Call, Request());

        Assert.Equal(RunnerRelayRefusal.NotAuthorized, result.Refusal);
        await this._reasoningCapture.DidNotReceiveWithAnyArgs().CapturesReasoningAsync(default, default);
    }

    // The same holds for a job this replica is not holding open: the refusal comes before the policy is read.
    [Fact]
    public async Task ACallForAJobThisReplicaDoesNotHold_ReadsNoReasoningCapturePolicy()
    {
        var relay = this.CreateRelay();

        var result = await relay.CompleteAsync(Call, Request());

        Assert.Equal(RunnerRelayRefusal.JobNotHeld, result.Refusal);
        await this._reasoningCapture.DidNotReceiveWithAnyArgs().CapturesReasoningAsync(default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheJobsReasoningCaptureDecision_ShapesTheOptionsTheProviderIsCalledWith(bool capturesReasoning)
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(capturesReasoning);

        bool? askedWith = null;
        await relay.CompleteAsync(
            Call,
            new RunnerRelayRequest(
                "reviewer-medium",
                [new ChatMessage(ChatRole.User, "hi")],
                decision =>
                {
                    askedWith = decision;
                    return new ChatOptions { ModelId = decision ? "captures" : "withholds" };
                },
                "k"));

        Assert.Equal(capturesReasoning, askedWith);
        await this._client.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.ModelId == (capturesReasoning ? "captures" : "withholds")),
            Arg.Any<CancellationToken>());
    }

    // The policy answers false wherever nothing has permitted this tenant's reasoning to be kept, including
    // for a job it cannot resolve to a client. The relay is where the completion is served from, so that
    // answer has to reach the options the provider is called with.
    [Fact]
    public async Task APolicyThatAnswersNo_MakesTheCompletionWithholdReasoning()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        bool? askedWith = null;
        await relay.CompleteAsync(
            Call,
            new RunnerRelayRequest(
                "reviewer-medium",
                [new ChatMessage(ChatRole.User, "hi")],
                decision =>
                {
                    askedWith = decision;
                    return new ChatOptions { ModelId = decision ? "captures" : "withholds" };
                },
                "k"));

        Assert.False(askedWith);
        await this._client.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.ModelId == "withholds"),
            Arg.Any<CancellationToken>());
    }

    // The order is the contract: options built from a decision that was not read yet would carry whatever
    // the caller shaped them with, and a provider called before the decision is known cannot honour it.
    [Fact]
    public async Task ThePolicyIsRead_BeforeTheOptionsAreBuiltAndBeforeTheProviderIsCalled()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        var steps = new List<string>();

        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                steps.Add("policy");
                return true;
            });
        this._client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                steps.Add("provider");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });

        await relay.CompleteAsync(
            Call,
            new RunnerRelayRequest(
                "reviewer-medium",
                [new ChatMessage(ChatRole.User, "hi")],
                _ =>
                {
                    steps.Add("options");
                    return null;
                },
                "k"));

        Assert.Equal(["policy", "options", "provider"], steps);
    }

    // A retry is answered from what the first attempt produced, and the tenant may have forbidden reasoning
    // in between. The answer is the completion it already paid for, without the reasoning it may not keep.
    [Fact]
    public async Task AReplayServedAfterThePolicyTurnedOff_CarriesTheCompletionWithoutTheReasoning()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.AnswersWithReasoning();
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(true);

        var first = await relay.CompleteAsync(Call, Request("same-key"));

        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);
        var replayed = await relay.CompleteAsync(Call, Request("same-key"));

        Assert.True(replayed.Replayed);
        Assert.DoesNotContain(
            replayed.Response!.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);
        Assert.Equal("the finding", replayed.Response.Text);

        // One provider call for two attempts: the retry was answered from the cache and redacted on the way
        // out, and not by a second completion that happened to say the same thing and charge for it again.
        await this._client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());

        // The spend happened on the first attempt and is reported the same way, or the executor under-reports it.
        Assert.Equal(first.Usage, replayed.Usage);
    }

    [Fact]
    public async Task AReplayForAJobThatCapturesReasoning_CarriesTheReasoningTheFirstAttemptProduced()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.AnswersWithReasoning();
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(true);

        var first = await relay.CompleteAsync(Call, Request("same-key"));
        var replayed = await relay.CompleteAsync(Call, Request("same-key"));

        Assert.False(first.Replayed);
        Assert.True(replayed.Replayed);

        // One provider call for two attempts: the reasoning came out of the cache and not out of a second
        // completion that happened to say the same thing.
        await this._client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
        Assert.Contains(
            replayed.Response!.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);
    }

    // A provider returns reasoning parts of its own accord, whatever the request asked for, so the first
    // completion is where a tenant that keeps none would lose it.
    [Fact]
    public async Task ACompletionForAJobThatWithholdsReasoning_CarriesNoneTheFirstTimeItIsServed()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.AnswersWithReasoning();
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await relay.CompleteAsync(Call, Request());

        Assert.DoesNotContain(
            result.Response!.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);
        Assert.Equal("the finding", result.Response.Text);
    }

    // What is held for a retry is the completion the provider returned, and the policy in force when the retry
    // arrives decides what the retry is served. Redacting what is held would make one restrictive attempt
    // permanent: the text was paid for once and there is no second call to produce it again.
    [Fact]
    public async Task ACompletionWithheldOnceAndReplayedUnderAPolicyThatCaptures_CarriesItsReasoningAgain()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.AnswersWithReasoning();
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        var withheld = await relay.CompleteAsync(Call, Request("same-key"));
        Assert.DoesNotContain(
            withheld.Response!.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);

        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(true);
        var replayed = await relay.CompleteAsync(Call, Request("same-key"));

        Assert.True(replayed.Replayed);
        Assert.Contains(
            replayed.Response!.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);

        // From the cache, not from a second completion: one call answered both attempts.
        await this._client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    // The parts of a served turn are built anew, so the provider metadata cleared for one attempt is still on
    // the completion held for the next. Cleared in place it would also leave a concurrent retry of the same
    // key reading an answer that is being redacted underneath it.
    [Fact]
    public async Task ClearingTheProviderMetadataOfOneAttempt_LeavesTheHeldCompletionCarryingIt()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.AnswersWithProviderMetadata();
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        await relay.CompleteAsync(Call, Request("same-key"));

        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(true);
        var replayed = await relay.CompleteAsync(Call, Request("same-key"));

        // From the cache and not from a second completion: one provider call answered both attempts, so the
        // metadata below is the metadata the held completion kept.
        Assert.True(replayed.Replayed);
        await this._client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());

        var text = Assert.Single(replayed.Response!.Messages).Contents.OfType<TextContent>().Single();
        Assert.NotNull(text.RawRepresentation);
        Assert.NotNull(text.AdditionalProperties);
    }

    // Provider metadata is the second place the reasoning sits: a thinking block or a signature on a message,
    // on the response, or on the raw payload either was read from. The counters stay, because budgets use them.
    [Fact]
    public async Task AWithheldCompletion_CarriesNoProviderMetadataAndKeepsItsCounters()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.AnswersWithProviderMetadata();
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        var served = (await relay.CompleteAsync(Call, Request())).Response!;

        Assert.Null(served.AdditionalProperties);
        Assert.Null(served.RawRepresentation);

        var message = Assert.Single(served.Messages);
        Assert.Null(message.AdditionalProperties);
        Assert.Null(message.RawRepresentation);
        Assert.DoesNotContain(message.Contents, content => content is TextReasoningContent);
        Assert.All(
            message.Contents,
            content =>
            {
                Assert.Null(content.AdditionalProperties);
                Assert.Null(content.RawRepresentation);
            });

        Assert.Equal("the finding", served.Text);
        Assert.Equal(1_000_000, served.Usage!.OutputTokenCount);
    }

    // A provider stores the state that continues a tool exchange in the properties beside the call: the Google
    // Vertex driver keeps the signed call part there, and Gemini refuses a continuation that arrives without
    // it. Clearing them stopped tool calling for every tenant that keeps no reasoning.
    [Fact]
    public async Task AWithheldCompletion_KeepsThePropertiesOfAToolCallAndItsResult()
    {
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this.AnswersWithAToolExchange();
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        var served = (await relay.CompleteAsync(Call, Request())).Response!;
        var contents = Assert.Single(served.Messages).Contents;

        var call = contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("a signed call part", call.AdditionalProperties!["thoughtSignature"]);

        var result = contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("a signed result part", result.AdditionalProperties!["thoughtSignature"]);

        // The payload the parts were read from still goes: a thinking block sits there on every kind of part.
        Assert.Null(call.RawRepresentation);
        Assert.Null(result.RawRepresentation);

        // The text beside them is redacted as before, so the carve-out reaches the tool exchange alone.
        Assert.Null(contents.OfType<TextContent>().Single().AdditionalProperties);
        Assert.DoesNotContain(contents, content => content is TextReasoningContent);
    }

    // The turn is copied and then rewritten, so the members a redaction has no business touching survive it.
    // Rebuilding the turn by construction dropped the timestamp.
    [Fact]
    public async Task AWithheldCompletion_KeepsWhatTheTurnCarriesBesideItsContent()
    {
        var stamped = DateTimeOffset.Parse("2026-03-04T05:06:07Z", CultureInfo.InvariantCulture);
        var relay = this.CreateRelay(budget: Budget(null, 0m));
        this._client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("private"), new TextContent("the finding")])
                    {
                        AuthorName = "the-reviewer",
                        MessageId = "message-9",
                        CreatedAt = stamped,
                    }));
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        var served = (await relay.CompleteAsync(Call, Request())).Response!;
        var message = Assert.Single(served.Messages);

        Assert.Equal(stamped, message.CreatedAt);
        Assert.Equal("the-reviewer", message.AuthorName);
        Assert.Equal("message-9", message.MessageId);
        Assert.Equal("the finding", message.Text);
    }

    /// <summary>Has the resolved client answer with a tool call, its result, and the text beside them.</summary>
    private void AnswersWithAToolExchange()
    {
        var call = new FunctionCallContent("call-1", "search_repository", new Dictionary<string, object?> { ["query"] = "budget" })
        {
            RawRepresentation = new { thinking = "private chain of thought" },
            AdditionalProperties = new AdditionalPropertiesDictionary { ["thoughtSignature"] = "a signed call part" },
        };

        var result = new FunctionResultContent("call-1", "three matches")
        {
            RawRepresentation = new { thinking = "private chain of thought" },
            AdditionalProperties = new AdditionalPropertiesDictionary { ["thoughtSignature"] = "a signed result part" },
        };

        var text = new TextContent("the finding")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["signature"] = "a thinking signature" },
        };

        this._client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new TextReasoningContent("private chain of thought"), call, result, text]))
                {
                    Usage = new UsageDetails { OutputTokenCount = 1_000_000 },
                });
    }

    /// <summary>Has the resolved client answer with a turn whose provider metadata carries the reasoning.</summary>
    private void AnswersWithProviderMetadata()
    {
        var text = new TextContent("the finding")
        {
            RawRepresentation = new { thinking = "private chain of thought" },
            AdditionalProperties = new AdditionalPropertiesDictionary { ["signature"] = "a thinking signature" },
        };

        // A reasoning part carries the same two fields, and a redaction that only cleared them on the parts it
        // keeps would hand back the reasoning part with its payload attached.
        var reasoning = new TextReasoningContent("private chain of thought")
        {
            RawRepresentation = new { thinking = "private chain of thought" },
            AdditionalProperties = new AdditionalPropertiesDictionary { ["signature"] = "a thinking signature" },
        };

        this._client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, [reasoning, text])
                    {
                        RawRepresentation = new { thinking = "private chain of thought" },
                        AdditionalProperties = new AdditionalPropertiesDictionary { ["thinking"] = "private chain of thought" },
                    })
                {
                    Usage = new UsageDetails { OutputTokenCount = 1_000_000 },
                    RawRepresentation = new { thinking = "private chain of thought" },
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["thinking"] = "private chain of thought" },
                });
    }

    /// <summary>Has the resolved client answer with a turn that carries reasoning beside its text.</summary>
    private void AnswersWithReasoning()
    {
        this._client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new TextReasoningContent("private chain of thought"), new TextContent("the finding")]))
                {
                    Usage = new UsageDetails { OutputTokenCount = 1_000_000 },
                });
    }
}
