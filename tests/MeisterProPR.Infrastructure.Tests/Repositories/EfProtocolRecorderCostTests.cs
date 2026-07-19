// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

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

    private static async Task<(Guid JobId, Guid ProtocolId, Guid ClientId)> SeedJobAndProtocolAsync(DbContextOptions<MeisterProPRDbContext> options)
    {
        var clientId = Guid.NewGuid();
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/test", "proj", "repo", 1, 1);
        job.SetAiConfig(ConnectionId, "gpt-4o");

        await using var seed = new MeisterProPRDbContext(options);
        seed.ReviewJobs.Add(job);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
            AiConnectionCategory = AiConnectionModelCategory.HighEffort,
            ModelId = "gpt-4o",
        };
        seed.ReviewJobProtocols.Add(protocol);
        await seed.SaveChangesAsync();

        return (job.Id, protocol.Id, clientId);
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
}
