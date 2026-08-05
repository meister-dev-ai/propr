// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Cost-wiring tests for <see cref="EfProtocolRecorder" /> using an EF Core in-memory database, so the
///     resolver → calculator → <see cref="ReviewJob.SetTierCost" /> → daily-sample flow runs without Postgres.
/// </summary>
public sealed class EfProtocolRecorderCostTests
{
    private static readonly Guid ConnectionId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private static DbContextOptions<MeisterProPRDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"EfProtocolRecorderCostTests-{Guid.NewGuid():N}")
            .Options;
    }

    private static async Task<(Guid JobId, Guid ProtocolId, Guid ClientId)> SeedJobAndProtocolAsync(
        DbContextOptions<MeisterProPRDbContext> options,
        string? protocolModelId = "gpt-4o",
        string? jobModelId = "gpt-4o")
    {
        var clientId = Guid.NewGuid();
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/test", "proj", "repo", 1, 1);
        job.SetAiConfig(ConnectionId, jobModelId);

        await using var seed = new MeisterProPRDbContext(options);
        seed.ReviewJobs.Add(job);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
            AiConnectionCategory = AiConnectionModelCategory.HighEffort,
            ModelId = protocolModelId,
        };
        seed.ReviewJobProtocols.Add(protocol);
        await seed.SaveChangesAsync();

        return (job.Id, protocol.Id, clientId);
    }

    // A protocol that never recorded which model it used was filed under "(default)", which matches no configured
    // model, so its tokens were counted and its cost was not. On one real job that silently dropped 21% of the
    // input from the total. The job knows the model it ran, so it is used rather than throwing the spend away.
    [Fact]
    public async Task SetCompletedAsync_WhenTheProtocolNamedNoModel_PricesItAgainstTheJobsModel()
    {
        var options = CreateOptions();
        var (jobId, protocolId, _) = await SeedJobAndProtocolAsync(options, protocolModelId: null);

        var resolver = Substitute.For<IModelPricingResolver>();
        resolver.ResolveAsync(ConnectionId, Arg.Any<AiConnectionModelCategory>(), "gpt-4o", Arg.Any<CancellationToken>())
            .Returns(new ModelPricing(2m, 10m, 1m));

        var recorder = new EfProtocolRecorder(new TestDbContextFactory(options), NullLogger<EfProtocolRecorder>.Instance, resolver);

        await recorder.SetCompletedAsync(protocolId, "Completed", 1_000_000, 500_000, 2, 1, null);

        await using var verify = new MeisterProPRDbContext(options);
        var storedJob = await verify.ReviewJobs.FirstAsync(j => j.Id == jobId);
        var entry = Assert.Single(storedJob.TokenBreakdown);

        Assert.Equal("gpt-4o", entry.ModelId);
        Assert.Equal(7m, entry.EstimatedCostUsd);
        // Priced against a model the call did not name, so it is an attribution rather than a measurement.
        Assert.True(entry.CostIsApproximate);
        Assert.True(storedJob.CostIsApproximate);
    }

    // Only when the job cannot name a model either is the spend genuinely unattributable.
    [Fact]
    public async Task SetCompletedAsync_WhenNeitherProtocolNorJobNamesAModel_LeavesItUnpriced()
    {
        var options = CreateOptions();
        var (jobId, protocolId, _) = await SeedJobAndProtocolAsync(options, protocolModelId: null, jobModelId: null);

        var resolver = Substitute.For<IModelPricingResolver>();
        resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<AiConnectionModelCategory>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ModelPricing?)null);

        var recorder = new EfProtocolRecorder(new TestDbContextFactory(options), NullLogger<EfProtocolRecorder>.Instance, resolver);

        await recorder.SetCompletedAsync(protocolId, "Completed", 1_000_000, 500_000, 2, 1, null);

        await using var verify = new MeisterProPRDbContext(options);
        var entry = Assert.Single((await verify.ReviewJobs.FirstAsync(j => j.Id == jobId)).TokenBreakdown);

        Assert.Equal("(default)", entry.ModelId);
        Assert.Null(entry.EstimatedCostUsd);
        Assert.Equal(1_000_000, entry.TotalInputTokens);
    }

    [Fact]
    public async Task SetCompletedAsync_WithResolvedPricing_PersistsTierCostJobTotalAndSampleCost()
    {
        var options = CreateOptions();
        var (jobId, protocolId, clientId) = await SeedJobAndProtocolAsync(options);

        var resolver = Substitute.For<IModelPricingResolver>();
        resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.HighEffort, "gpt-4o", Arg.Any<CancellationToken>())
            .Returns(new ModelPricing(2m, 10m, 1m));

        var recorder = new EfProtocolRecorder(
            new TestDbContextFactory(options),
            NullLogger<EfProtocolRecorder>.Instance,
            resolver);

        // 1_000_000 input, 500_000 output, no cache/reasoning -> 1_000_000*2/1e6 + 500_000*10/1e6 = 2 + 5 = 7.
        await recorder.SetCompletedAsync(protocolId, "Completed", 1_000_000, 500_000, 2, 1, null);

        await using var verify = new MeisterProPRDbContext(options);
        var storedJob = await verify.ReviewJobs.FirstAsync(j => j.Id == jobId);
        Assert.Equal(7m, storedJob.TotalEstimatedCostUsd);
        Assert.False(storedJob.CostIsApproximate);

        var entry = Assert.Single(storedJob.TokenBreakdown);
        Assert.Equal(7m, entry.EstimatedCostUsd);
        Assert.False(entry.CostIsApproximate);

        var sample = await verify.ClientTokenUsageSamples.FirstAsync(s => s.ClientId == clientId);
        Assert.Equal(7m, sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task SetCompletedAsync_WhenPricingUnresolved_RecordsTokensWithNullCostAndApproximate()
    {
        var options = CreateOptions();
        var (jobId, protocolId, clientId) = await SeedJobAndProtocolAsync(options);

        var resolver = Substitute.For<IModelPricingResolver>();
        resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<AiConnectionModelCategory>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ModelPricing?)null);

        var recorder = new EfProtocolRecorder(
            new TestDbContextFactory(options),
            NullLogger<EfProtocolRecorder>.Instance,
            resolver);

        await recorder.SetCompletedAsync(protocolId, "Completed", 1000, 500, 2, 1, null);

        await using var verify = new MeisterProPRDbContext(options);
        var storedJob = await verify.ReviewJobs.FirstAsync(j => j.Id == jobId);

        // Tokens are still recorded.
        Assert.Equal(1000, storedJob.TotalInputTokensAggregated);
        Assert.Equal(500, storedJob.TotalOutputTokensAggregated);

        // Cost is null (never a misleading zero) and flagged approximate.
        Assert.Null(storedJob.TotalEstimatedCostUsd);
        Assert.True(storedJob.CostIsApproximate);

        var entry = Assert.Single(storedJob.TokenBreakdown);
        Assert.Null(entry.EstimatedCostUsd);
        Assert.True(entry.CostIsApproximate);

        var sample = await verify.ClientTokenUsageSamples.FirstAsync(s => s.ClientId == clientId);
        Assert.Null(sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task SetCompletedAsync_WhenResolverThrows_StillRecordsTokens()
    {
        var options = CreateOptions();
        var (jobId, protocolId, _) = await SeedJobAndProtocolAsync(options);

        var resolver = Substitute.For<IModelPricingResolver>();
        resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<AiConnectionModelCategory>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ModelPricing?>(_ => throw new InvalidOperationException("pricing lookup failed"));

        var recorder = new EfProtocolRecorder(
            new TestDbContextFactory(options),
            NullLogger<EfProtocolRecorder>.Instance,
            resolver);

        var exception = await Record.ExceptionAsync(() =>
            recorder.SetCompletedAsync(protocolId, "Completed", 1000, 500, 2, 1, null));

        Assert.Null(exception);

        await using var verify = new MeisterProPRDbContext(options);
        var storedJob = await verify.ReviewJobs.FirstAsync(j => j.Id == jobId);
        Assert.Equal(1000, storedJob.TotalInputTokensAggregated);
        Assert.Equal(500, storedJob.TotalOutputTokensAggregated);
        Assert.Null(storedJob.TotalEstimatedCostUsd);
    }

    [Fact]
    public async Task BeginForThreadPassAsync_OwnsTheTraceRecordByThePassRatherThanAReviewJob()
    {
        var options = CreateOptions();
        var pass = await SeedThreadPassAsync(options);

        var recorder = new EfProtocolRecorder(
            new TestDbContextFactory(options),
            NullLogger<EfProtocolRecorder>.Instance);

        var protocolId = await recorder.BeginForThreadPassAsync(pass.Id, 1, "thread-17-code-change", "gpt-4o");

        await using var verify = new MeisterProPRDbContext(options);
        var stored = await verify.ReviewJobProtocols.FirstAsync(p => p.Id == protocolId);
        Assert.Equal(pass.Id, stored.ThreadPassJobId);
        Assert.Null(stored.JobId);
        Assert.Equal("thread-17-code-change", stored.Label);
    }

    [Fact]
    public async Task SetCompletedAsync_ForAThreadPassProtocol_MovesTheSpendOntoThePassAndTheDailySample()
    {
        var options = CreateOptions();
        var pass = await SeedThreadPassAsync(options);

        var resolver = Substitute.For<IModelPricingResolver>();
        resolver.ResolveAsync(ConnectionId, Arg.Any<AiConnectionModelCategory>(), "gpt-4o", Arg.Any<CancellationToken>())
            .Returns(new ModelPricing(2m, 10m, 1m));

        var recorder = new EfProtocolRecorder(
            new TestDbContextFactory(options),
            NullLogger<EfProtocolRecorder>.Instance,
            resolver);

        // Two threads, each closed as its own trace record.
        var first = await recorder.BeginForThreadPassAsync(pass.Id, 1, "thread-17-code-change", "gpt-4o");
        await recorder.SetCompletedAsync(first, "Resolved", 1_000_000, 500_000, 1, 0, null);
        var second = await recorder.BeginForThreadPassAsync(pass.Id, 1, "thread-18-conversational", "gpt-4o");
        await recorder.SetCompletedAsync(second, "NotResolved", 1_000_000, 0, 1, 0, null);

        await using var verify = new MeisterProPRDbContext(options);
        var storedPass = await verify.ThreadPassJobs.FirstAsync(p => p.Id == pass.Id);

        Assert.Equal(2_000_000, storedPass.TotalInputTokens);
        Assert.Equal(500_000, storedPass.TotalOutputTokens);
        Assert.Equal(9m, storedPass.TotalEstimatedCostUsd);
        Assert.False(storedPass.CostIsApproximate);

        var sample = await verify.ClientTokenUsageSamples.FirstAsync(s => s.ClientId == pass.ClientId);
        Assert.Equal(2_000_000, sample.InputTokens);
        Assert.Equal(500_000, sample.OutputTokens);
        Assert.Equal(9m, sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task SetCompletedAsync_ForAnUnpricedThreadPassProtocol_RecordsTokensAndFlagsTheCostApproximate()
    {
        var options = CreateOptions();
        var pass = await SeedThreadPassAsync(options);

        var recorder = new EfProtocolRecorder(
            new TestDbContextFactory(options),
            NullLogger<EfProtocolRecorder>.Instance);

        var protocolId = await recorder.BeginForThreadPassAsync(pass.Id, 1, "thread-17-code-change", "gpt-4o");
        await recorder.SetCompletedAsync(protocolId, "Resolved", 1000, 500, 1, 0, null);

        await using var verify = new MeisterProPRDbContext(options);
        var storedPass = await verify.ThreadPassJobs.FirstAsync(p => p.Id == pass.Id);

        Assert.Equal(1000, storedPass.TotalInputTokens);
        Assert.Null(storedPass.TotalEstimatedCostUsd);
        Assert.True(storedPass.CostIsApproximate);
    }

    private static async Task<ThreadPassJob> SeedThreadPassAsync(DbContextOptions<MeisterProPRDbContext> options)
    {
        var pass = new ThreadPassJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/test",
            "proj",
            "repo",
            1,
            1,
            "1",
            "1|abc");
        pass.SetAiConfig(ConnectionId, "gpt-4o");

        await using var seed = new MeisterProPRDbContext(options);
        seed.ThreadPassJobs.Add(pass);
        await seed.SaveChangesAsync();
        return pass;
    }
}
