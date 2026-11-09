// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Token accounting of one job against a real PostgreSQL database while many of its passes close at once, as the
///     parallel file passes of a large review do.
/// </summary>
[Collection("PostgresIntegration4")]
public sealed class EfProtocolRecorderConcurrencyTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const int PassCount = 24;
    private const long PassInputTokens = 1_000;
    private const long PassOutputTokens = 10;
    private const long OutOfLoopInputTokens = 100;
    private const long OutOfLoopOutputTokens = 1;

    private DbContextOptions<MeisterProPRDbContext> _options = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentPassesOfOneJob_AllReachTheJobTotalsAndTheBreakdown()
    {
        var (jobId, protocolIds) = await this.SeedJobWithPassesAsync();
        var recorder = new EfProtocolRecorder(new TestDbContextFactory(this._options), NullLogger<EfProtocolRecorder>.Instance);

        // Every pass closes and reports one out-of-loop call at the same time as the others.
        await Task.WhenAll(
            protocolIds.SelectMany(protocolId => new[]
            {
                recorder.SetCompletedAsync(protocolId, "Completed", PassInputTokens, PassOutputTokens, 1, 0, null),
                recorder.AddTokensAsync(
                    protocolId,
                    OutOfLoopInputTokens,
                    OutOfLoopOutputTokens,
                    AiConnectionModelCategory.Default,
                    "triage-model"),
            }));

        await using var verify = new MeisterProPRDbContext(this._options);
        var job = await verify.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);

        const long expectedInput = PassCount * (PassInputTokens + OutOfLoopInputTokens);
        const long expectedOutput = PassCount * (PassOutputTokens + OutOfLoopOutputTokens);
        Assert.Equal(expectedInput, job.TotalInputTokensAggregated);
        Assert.Equal(expectedOutput, job.TotalOutputTokensAggregated);
        Assert.Equal(expectedInput, job.TokenBreakdown.Sum(entry => entry.TotalInputTokens));
        Assert.Equal(expectedOutput, job.TokenBreakdown.Sum(entry => entry.TotalOutputTokens));

        var passTier = Assert.Single(job.TokenBreakdown, entry => entry.ConnectionCategory == AiConnectionModelCategory.LowEffort);
        Assert.Equal(PassCount * PassInputTokens, passTier.TotalInputTokens);
        var outOfLoop = Assert.Single(job.TokenBreakdown, entry => entry.ConnectionCategory == AiConnectionModelCategory.Default);
        Assert.Equal(PassCount * OutOfLoopInputTokens, outOfLoop.TotalInputTokens);
    }

    private async Task<(Guid JobId, IReadOnlyList<Guid> ProtocolIds)> SeedJobWithPassesAsync()
    {
        await using var seed = new MeisterProPRDbContext(this._options);
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/test", "proj", "repo", 1, 1);
        seed.ReviewJobs.Add(job);

        var protocolIds = new List<Guid>(PassCount);
        for (var index = 0; index < PassCount; index++)
        {
            var protocol = new ReviewJobProtocol
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AttemptNumber = 1,
                StartedAt = DateTimeOffset.UtcNow,
                Label = $"src/file-{index}.cs",
                AiConnectionCategory = AiConnectionModelCategory.LowEffort,
                ModelId = "review-model",
            };
            seed.ReviewJobProtocols.Add(protocol);
            protocolIds.Add(protocol.Id);
        }

        await seed.SaveChangesAsync();
        return (job.Id, protocolIds);
    }
}
