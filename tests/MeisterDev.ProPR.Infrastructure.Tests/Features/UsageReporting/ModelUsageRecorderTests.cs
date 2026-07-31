// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Tests.AI;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.Infrastructure.Features.UsageReporting;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

/// <summary>
///     What counts the tokens an insight model call spends. Nothing else does: these calls run outside a review
///     job, so the protocol that normally records usage is not there, and an unrecorded call is spend that no cost
///     report shows and no client budget cap is measured against.
/// </summary>
public sealed class CodeInsightModelUsageRecorderTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task RecordAsync_WritesTheCountersAndThePricedCostToTheClientsDailySample()
    {
        var options = CreateOptions();
        var recorder = CreateRecorder(options);

        await recorder.RecordAsync(ClientId, CreateRuntime(), CreateResponse(1_000, 500, cachedInput: 200));

        await using var db = new MeisterProPRDbContext(options);
        var sample = Assert.Single(db.ClientTokenUsageSamples);
        Assert.Equal(ClientId, sample.ClientId);
        Assert.Equal("gpt-4o-mini", sample.ModelId);
        Assert.Equal(1_000, sample.InputTokens);
        Assert.Equal(500, sample.OutputTokens);
        Assert.Equal(200, sample.CachedInputTokens);

        // Priced from the model that answered: 800 uncached input at $1/1M, 200 cached at $0.25/1M, 500 output
        // at $4/1M.
        Assert.Equal(0.00285m, sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task RecordAsync_KeysTheSampleByModelLogicalModelAndProviderSoInsightSpendJoinsReviewSpend()
    {
        var options = CreateOptions();
        var recorder = CreateRecorder(options);

        await recorder.RecordAsync(ClientId, CreateRuntime(logicalModelName: "cheap-classifier"), CreateResponse(10, 5));

        await using var db = new MeisterProPRDbContext(options);
        var sample = Assert.Single(db.ClientTokenUsageSamples);
        Assert.Equal("cheap-classifier", sample.LogicalModelName);
        Assert.Equal(nameof(AiProviderKind.AzureOpenAi), sample.ProviderKind);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), sample.Date);
    }

    [Fact]
    public async Task RecordAsync_AccumulatesRepeatedCallsIntoTheOneRowForTheDay()
    {
        var options = CreateOptions();
        var recorder = CreateRecorder(options);
        var runtime = CreateRuntime();

        await recorder.RecordAsync(ClientId, runtime, CreateResponse(100, 50));
        await recorder.RecordAsync(ClientId, runtime, CreateResponse(300, 20));

        // One row per client, model, logical model, provider, and day: a classification sweep is thousands of
        // small calls, and a row each would turn the usage table into a call log.
        await using var db = new MeisterProPRDbContext(options);
        var sample = Assert.Single(db.ClientTokenUsageSamples);
        Assert.Equal(400, sample.InputTokens);
        Assert.Equal(70, sample.OutputTokens);
    }

    [Fact]
    public async Task RecordAsync_WithNoUsagePayload_RecordsNothing()
    {
        var options = CreateOptions();
        var recorder = CreateRecorder(options);

        // A provider that reports no usage is not a call that cost nothing. Writing a zero row would state the
        // stronger of the two as if it were measured.
        await recorder.RecordAsync(ClientId, CreateRuntime(), new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await using var db = new MeisterProPRDbContext(options);
        Assert.Empty(db.ClientTokenUsageSamples);
    }

    [Fact]
    public async Task RecordAsync_WhenTheWriteFails_DoesNotThrow()
    {
        var factory = Substitute.For<IDbContextFactory<MeisterProPRDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns<Task<MeisterProPRDbContext>>(_ => throw new InvalidOperationException("the database is gone"));

        var recorder = new ModelUsageRecorder(
            factory,
            NullLogger<ModelUsageRecorder>.Instance);

        // The tokens are already spent. Failing the classification that spent them would trade a wrong number
        // for lost work.
        await recorder.RecordAsync(ClientId, CreateRuntime(), CreateResponse(10, 5));
    }

    private static ModelUsageRecorder CreateRecorder(DbContextOptions<MeisterProPRDbContext> options)
    {
        // The recorder writes on a fresh context per call, as it does in the host; the test reads through its own.
        return new ModelUsageRecorder(
            new TestDbContextFactory(options),
            NullLogger<ModelUsageRecorder>.Instance);
    }

    private static DbContextOptions<MeisterProPRDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"insight-usage-{Guid.NewGuid()}")
            .Options;
    }

    private static ChatResponse CreateResponse(long input, long output, long cachedInput = 0)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = input,
                OutputTokenCount = output,
                CachedInputTokenCount = cachedInput,
            },
        };
    }

    private static IResolvedAiChatRuntime CreateRuntime(string? logicalModelName = null)
    {
        var model = new AiConfiguredModelDto(
            Guid.NewGuid(),
            "gpt-4o-mini",
            "Classifier",
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto],
            InputCostPer1MUsd: 1m,
            OutputCostPer1MUsd: 4m,
            CachedInputCostPer1MUsd: 0.25m);

        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.Connection.Returns(AiConnectionTestFactory.CreateConnection(ClientId, [model]));
        runtime.Model.Returns(model);
        runtime.LogicalModelName.Returns(logicalModelName);
        return runtime;
    }
}
