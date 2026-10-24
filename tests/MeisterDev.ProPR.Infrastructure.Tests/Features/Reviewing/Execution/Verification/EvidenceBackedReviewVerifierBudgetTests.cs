// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    private const string ConfirmedBudgetVerdict = """{"verdict":"confirmed","reason":"The lookup may return null."}""";

    [Theory]
    [InlineData("")]
    [InlineData("{\"verdict\":\"confirmed\",\"reason\":\"")]
    [InlineData(ConfirmedBudgetVerdict)]
    public async Task ALengthFinishReason_RetriesEvenWithoutUsageOrWithParseableJson(string text)
    {
        var run = await RunBudgetResponsesAsync(
            [BudgetResponse(text, ChatFinishReason.Length), BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20)]);

        Assert.Equal(FinalGateDecision.PublishDisposition, run.Outcome.RecommendedDisposition);
        Assert.Equal([4000, 8000], run.Options.Select(option => option.MaxOutputTokens));
        Assert.All(run.Options, option => Assert.Null(option.Reasoning));
        Assert.IsType<NoneChatToolMode>(run.Options[1].ToolMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"verdict\":\"confirmed\",\"reason\":\"")]
    [InlineData("{\"verdict\":\"unknown\"}")]
    public async Task AnUnusableVerdictAtTheUsageCeiling_RetriesWhenFinishMetadataIsMissing(string text)
    {
        var run = await RunBudgetResponsesAsync([BudgetResponse(text, null, 4000, 4000), BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20)]);

        Assert.Equal(FinalGateDecision.PublishDisposition, run.Outcome.RecommendedDisposition);
        Assert.Equal(2, run.Options.Count);
    }

    [Fact]
    public async Task AnUnusableVerdictAtTheUsageCeiling_RetriesWhenTheGatewayReportsStop()
    {
        var run = await RunBudgetResponsesAsync(
            [BudgetResponse("", ChatFinishReason.Stop, 4000, 4000), BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20)]);

        Assert.Equal(FinalGateDecision.PublishDisposition, run.Outcome.RecommendedDisposition);
        Assert.Equal(2, run.Options.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"verdict\":\"confirmed\"")]
    public async Task AnUnusableVerdictBelowTheCeiling_IsWithheldWithoutBudgetRetry(string text)
    {
        var run = await RunBudgetResponsesAsync([BudgetResponse(text, ChatFinishReason.Stop, 3999)]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.Unparseable, run.Outcome.JudgeDegradation);
        Assert.Single(run.Options);
    }

    [Fact]
    public async Task ACompleteVerdictAtTheUsageCeiling_IsAcceptedWhenTheProviderReportsStop()
    {
        var run = await RunBudgetResponsesAsync([BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 4000)]);

        Assert.Equal(FinalGateDecision.PublishDisposition, run.Outcome.RecommendedDisposition);
        Assert.Single(run.Options);
    }

    [Theory]
    [InlineData("")]
    [InlineData(ConfirmedBudgetVerdict)]
    public async Task ARepeatedLengthFinishReason_IsWithheldWithTheOutputLimitReason(string retryText)
    {
        var run = await RunBudgetResponsesAsync([BudgetResponse("", ChatFinishReason.Length, 4000), BudgetResponse(retryText, ChatFinishReason.Length)]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.OutputLimit, run.Outcome.JudgeDegradation);
        Assert.Null(run.Outcome.JudgeVerdict);
        Assert.Equal(2, run.Options.Count);
    }

    [Fact]
    public async Task ARepeatedUsageCeiling_IsWithheldWithTheOutputLimitReason()
    {
        var run = await RunBudgetResponsesAsync([BudgetResponse("", null, 4000, 4000), BudgetResponse("", null, 8000, 8000)]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.OutputLimit, run.Outcome.JudgeDegradation);
        Assert.Equal(2, run.Options.Count);
    }

    [Fact]
    public async Task ACappedToolRequestOnTheRetry_IsWithheldWithTheOutputLimitReason()
    {
        var retry = ToolCallResponse("partial", "get_file_content", """{"path":"src/Other.cs","branch":"source","startLine":1,"endLine":5}""");
        retry.FinishReason = ChatFinishReason.Length;
        var run = await RunBudgetResponsesAsync([BudgetResponse("", ChatFinishReason.Length), retry]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.OutputLimit, run.Outcome.JudgeDegradation);
        await run.Tools.DidNotReceive().GetFileContentAsync("src/Other.cs", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AToolRequestAtTheRetryUsageCeiling_IsWithheldWithTheOutputLimitReason(bool reportsStop)
    {
        var retry = ToolCallResponse("unexpected", "get_file_content", """{"path":"src/Other.cs","branch":"source","startLine":1,"endLine":5}""");
        retry.FinishReason = reportsStop ? ChatFinishReason.Stop : null;
        retry.Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 8000 };

        var run = await RunBudgetResponsesAsync([BudgetResponse("", ChatFinishReason.Length), retry]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.OutputLimit, run.Outcome.JudgeDegradation);
        Assert.Equal([4000, 8000], run.Options.Select(option => option.MaxOutputTokens));
        Assert.IsType<NoneChatToolMode>(run.Options[1].ToolMode);
        await run.Tools.DidNotReceive().GetFileContentAsync("src/Other.cs", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AToolRequestAtTheUsageCeilingAfterTheLookupLimit_ReceivesTheBudgetRetry(bool reportsStop)
    {
        var responses = Enumerable.Range(1, 4)
            .Select(index => ToolCallResponse($"read-{index}", "get_file_content", """{"path":"src/Other.cs","branch":"source","startLine":1,"endLine":5}"""))
            .ToList();
        var capped = ToolCallResponse("unexpected", "get_file_content", """{"path":"src/Other.cs","branch":"source","startLine":1,"endLine":5}""");
        capped.FinishReason = reportsStop ? ChatFinishReason.Stop : null;
        capped.Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 4000 };
        responses.Add(capped);
        responses.Add(BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20));

        var run = await RunBudgetResponsesAsync(responses.ToArray());

        Assert.Equal(FinalGateDecision.PublishDisposition, run.Outcome.RecommendedDisposition);
        Assert.Equal([4000, 4000, 4000, 4000, 4000, 8000], run.Options.Select(option => option.MaxOutputTokens));
        Assert.All(run.Options.Skip(4), option => Assert.IsType<NoneChatToolMode>(option.ToolMode));
        await run.Tools.Received(4).GetFileContentAsync("src/Other.cs", "source", 1, 5, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExecutableToolRequestAtTheUsageCeiling_RunsWithoutBudgetRetry(bool reportsStop)
    {
        var lookup = ToolCallResponse("read", "get_file_content", """{"path":"src/Other.cs","branch":"source","startLine":1,"endLine":5}""");
        lookup.FinishReason = reportsStop ? ChatFinishReason.Stop : null;
        lookup.Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 4000 };

        var run = await RunBudgetResponsesAsync([lookup, BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20)]);

        Assert.Equal(FinalGateDecision.PublishDisposition, run.Outcome.RecommendedDisposition);
        Assert.Equal([4000, 4000], run.Options.Select(option => option.MaxOutputTokens));
        Assert.All(run.Options, option => Assert.Null(option.ToolMode));
        await run.Tools.Received(1).GetFileContentAsync("src/Other.cs", "source", 1, 5, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProviderErrorTakesPrecedenceOverAnOutputLimit(bool onRetry)
    {
        var error = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [new TextContent(ConfirmedBudgetVerdict), new ErrorContent("The stream failed.") { ErrorCode = "server_error" }]))
        {
            FinishReason = ChatFinishReason.Length,
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = onRetry ? 8000 : 4000 },
        };
        var run = await RunBudgetResponsesAsync(onRetry ? [BudgetResponse("", ChatFinishReason.Length), error] : [error]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.ProviderError, run.Outcome.JudgeDegradation);
        Assert.Equal(onRetry ? 2 : 1, run.Options.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusalOrContentFilter_CannotPublishEvenWithACompleteVerdict(bool contentFilter)
    {
        var response = BudgetResponse(ConfirmedBudgetVerdict, contentFilter ? ChatFinishReason.ContentFilter : ChatFinishReason.Length, 4000);
        if (!contentFilter)
        {
            response.Messages[0].Contents.Add(new ErrorContent("Request refused.") { ErrorCode = "Refusal" });
        }

        var run = await RunBudgetResponsesAsync([response]);

        AssertWithheld(run.Outcome);
        Assert.Single(run.Options);
    }

    [Fact]
    public async Task AFailedRecoveryWithoutOutputOrUsage_RemainsAProviderError()
    {
        var run = await RunBudgetResponsesAsync([BudgetResponse("", ChatFinishReason.Length), BudgetResponse("", ChatFinishReason.Stop)]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.ProviderError, run.Outcome.JudgeDegradation);
        Assert.Equal(2, run.Options.Count);
    }

    [Fact]
    public async Task ARecoveryWithMalformedOutputBelowItsBudget_RemainsUnparseable()
    {
        var run = await RunBudgetResponsesAsync([BudgetResponse("", ChatFinishReason.Length), BudgetResponse("invalid", ChatFinishReason.Stop, 10)]);

        AssertWithheld(run.Outcome);
        Assert.Equal(EvidenceJudgeDegradations.Unparseable, run.Outcome.JudgeDegradation);
        Assert.Equal(2, run.Options.Count);
    }

    [Fact]
    public async Task BothBudgetAttempts_AreRecordedAndChargedWithOutputLimitMetadata()
    {
        var run = await RunBudgetResponsesAsync(
            [BudgetResponse("", ChatFinishReason.Length, 4000, 4000), BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20)]);
        var aiCalls = run.Recorder.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IProtocolRecorder.RecordAiCallAsync)).ToList();
        var tokenCalls = run.Recorder.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IProtocolRecorder.AddTokensAsync)).ToList();
        var limitCall = Assert.Single(
            run.Recorder.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IProtocolRecorder.RecordVerificationEventAsync)
                                                       && Equals(call.GetArguments()[1], ReviewProtocolEventNames.EvidenceJudgeOutputLimit)));
        using var metadata = JsonDocument.Parse((string)limitCall.GetArguments()[3]!);

        Assert.Equal(2, aiCalls.Count);
        Assert.Equal(2, tokenCalls.Count);
        Assert.Equal(4020L, tokenCalls.Sum(call => (long)call.GetArguments()[2]!));
        Assert.All(tokenCalls, call => Assert.Equal("judge-model", call.GetArguments()[4]));
        Assert.Equal(4000, metadata.RootElement.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(4000, metadata.RootElement.GetProperty("reasoningTokens").GetInt64());
        Assert.Equal(8000, metadata.RootElement.GetProperty("retryMaxOutputTokens").GetInt32());
        Assert.True(metadata.RootElement.GetProperty("retryScheduled").GetBoolean());
    }

    [Fact]
    public async Task OutputRecovery_PreservesCompletedToolResultsAndDiscardsPartialOutput()
    {
        var run = await RunBudgetResponsesAsync(
        [
            ToolCallResponse("read", "get_file_content", """{"path":"src/Other.cs","branch":"source","startLine":1,"endLine":5}"""),
            BudgetResponse("{\"verdict\":\"confirmed\",\"reason\":\"partial", ChatFinishReason.Length, 4000),
            BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20),
        ]);

        Assert.Equal(FinalGateDecision.PublishDisposition, run.Outcome.RecommendedDisposition);
        Assert.Equal(3, run.Options.Count);
        var toolResults = run.Messages[2].SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Equal("read", Assert.Single(toolResults).CallId);
        Assert.DoesNotContain(run.Messages[2], message => message.Text.Contains("\"partial", StringComparison.Ordinal));
        await run.Tools.Received(1).GetFileContentAsync("src/Other.cs", "source", 1, 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CustomOutputBudgets_ReachBothAttempts()
    {
        var run = await RunBudgetResponsesAsync(
            [BudgetResponse("", ChatFinishReason.Length), BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20)],
            new AiReviewOptions { EvidenceJudgeMaxOutputTokens = 6000, EvidenceJudgeRetryMaxOutputTokens = 12000 });

        Assert.Equal([6000, 12000], run.Options.Select(option => option.MaxOutputTokens));
    }

    [Fact]
    public async Task ProductionDependencyInjection_PassesConfiguredBudgetsToTheJudge()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AI_EVIDENCE_JUDGE_MAX_OUTPUT_TOKENS"] = "6000",
                ["AI_EVIDENCE_JUDGE_RETRY_MAX_OUTPUT_TOKENS"] = "12000",
                ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
                ["ADO_STUB_PR"] = "true",
                ["MEISTER_JWT_SECRET"] = "test-judge-budget-jwt-secret-32-characters!",
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureSupport(configuration);
        services.AddReviewingExecution();
        services.AddSingleton(Substitute.For<IProtocolRecorder>());
        using var provider = services.BuildServiceProvider();
        var verifier = provider.GetRequiredService<EvidenceBackedReviewVerifier>();
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        var options = new List<ChatOptions>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                options.Add(call.ArgAt<ChatOptions>(1));
                return options.Count == 1 ? BudgetResponse("", ChatFinishReason.Length) : BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20);
            });

        await verifier.VerifyAsync([CreateWorkItem("src/Lookup.cs", 1)], [], new ReviewVerificationContext(tools, "source", judge, "judge-model"));

        Assert.Equal([6000, 12000], options.Select(option => option.MaxOutputTokens));
    }

    [Theory]
    [InlineData(0, 8000)]
    [InlineData(4000, 32769)]
    [InlineData(4000, 4000)]
    [InlineData(8000, 4000)]
    public void InvalidJudgeOutputBudgets_AreRejectedByDirectConstruction(int initial, int retry)
    {
        Assert.Throws<ValidationException>(() => new EvidenceBackedReviewVerifier(
            reviewOptions: Microsoft.Extensions.Options.Options.Create(
                new AiReviewOptions { EvidenceJudgeMaxOutputTokens = initial, EvidenceJudgeRetryMaxOutputTokens = retry })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecoveryFailure_RespectsProviderFailureAndTheExistingClaimDeadline(bool timeout)
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        var tokens = new List<CancellationToken>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                tokens.Add(call.ArgAt<CancellationToken>(2));
                if (tokens.Count == 1)
                {
                    return BudgetResponse("", ChatFinishReason.Length);
                }

                if (timeout)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, tokens[^1]);
                }

                throw new HttpRequestException("Provider unavailable.");
            });
        // The claim's time limit has to cover the anchor read, the first judge call and the start of the recovery
        // call, and a cold test process on a busy machine can need more than a second for them. The recovery call
        // then hangs until the limit, so the timeout case takes about five seconds.
        var outcomes = await new EvidenceBackedReviewVerifier(judgeCallTimeout: TimeSpan.FromSeconds(timeout ? 5 : 10)).VerifyAsync(
            [CreateWorkItem("src/Lookup.cs", 1)], [], new ReviewVerificationContext(tools, "source", judge, "judge-model"));

        AssertWithheld(Assert.Single(outcomes));
        Assert.Equal(timeout ? EvidenceJudgeDegradations.Timeout : EvidenceJudgeDegradations.ProviderError, outcomes[0].JudgeDegradation);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(tokens[0], tokens[1]);
    }

    [Fact]
    public async Task CallerCancellation_DuringRecoveryPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        var callCount = 0;
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (++callCount == 1)
                {
                    return BudgetResponse("", ChatFinishReason.Length);
                }

                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2));
                return BudgetResponse(ConfirmedBudgetVerdict, ChatFinishReason.Stop, 20);
            });
        var run = new EvidenceBackedReviewVerifier().VerifyAsync(
            [CreateWorkItem("src/Lookup.cs", 1)], [], new ReviewVerificationContext(tools, "source", judge, "judge-model"), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, callCount);
    }

    private static ChatResponse BudgetResponse(string text, ChatFinishReason? finishReason, long? outputTokens = null, long? reasoningTokens = null)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = finishReason,
            Usage = outputTokens.HasValue
                ? new UsageDetails { InputTokenCount = 100, OutputTokenCount = outputTokens, ReasoningTokenCount = reasoningTokens }
                : null,
        };
    }

    private static async Task<BudgetRunResult> RunBudgetResponsesAsync(ChatResponse[] responses, AiReviewOptions? options = null)
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        var seenOptions = new List<ChatOptions>();
        var seenMessages = new List<List<ChatMessage>>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seenOptions.Add(call.ArgAt<ChatOptions>(1));
                seenMessages.Add(call.ArgAt<IEnumerable<ChatMessage>>(0).ToList());
                return responses[Math.Min(seenOptions.Count - 1, responses.Length - 1)];
            });
        var recorder = Substitute.For<IProtocolRecorder>();
        var outcomes =
            await new EvidenceBackedReviewVerifier(recorder, reviewOptions: Microsoft.Extensions.Options.Options.Create(options ?? new AiReviewOptions()))
                .VerifyAsync(
                    [CreateWorkItem("src/Lookup.cs", 1)], [], new ReviewVerificationContext(tools, "source", judge, "judge-model", ProtocolId: Guid.NewGuid()));
        return new BudgetRunResult(Assert.Single(outcomes), seenOptions, seenMessages, tools, recorder);
    }

    private sealed record BudgetRunResult(
        VerificationOutcome Outcome,
        List<ChatOptions> Options,
        List<List<ChatMessage>> Messages,
        IReviewContextTools Tools,
        IProtocolRecorder Recorder);
}
