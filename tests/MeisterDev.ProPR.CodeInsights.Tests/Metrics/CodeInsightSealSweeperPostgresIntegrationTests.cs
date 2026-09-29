// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Persistence;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

/// <summary>
///     The seal sweep selects its candidates with queries that only PostgreSQL can translate and execute the way
///     production does. The in-memory provider evaluates aggregates with LINQ to Objects and accepts queries that
///     PostgreSQL rejects, so the sweep is run here against a real database.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class CodeInsightSealSweeperPostgresIntegrationTests(PostgresContainerFixture fixture)
    : IAsyncLifetime
{
    private static readonly Guid EarlierJobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LaterJobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ICodeInsightMetricSealer _sealer = Substitute.For<ICodeInsightMetricSealer>();
    private readonly IPullRequestFetcher _pullRequests = Substitute.For<IPullRequestFetcher>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

    private readonly string _keysDirectory = Path.Combine(
        Path.GetTempPath(),
        $"MeisterDev.ProPR.CodeInsightSealSweeperPostgresIntegrationTests.{Guid.NewGuid():N}");

    private ServiceProvider? _dataProtection;
    private CodeInsightFindingStore _store = null!;
    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private CodeInsightSealSweeper _sweeper = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        this._store = new CodeInsightFindingStore(this._dbContext, this.CreateCodec());

        var now = DateTimeOffset.UtcNow;
        this._dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId,
                Slug = "seal-" + this._tenantId.ToString("N"),
                DisplayName = "Seal Sweep Test Tenant",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = this._tenantId,
                DisplayName = "Seal Sweep Test Client",
                IsActive = true,
                CreatedAt = now,
            });
        await this._dbContext.SaveChangesAsync();

        // The database is shared with the other tests in the collection, so the gate is open for this test's
        // client alone. Aggregates other tests left behind stay out of the candidate set.
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => (Guid)call[0] == this._clientId);

        this._sealer
            .SealAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        this._pullRequests
            .FetchRefAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(new PullRequestRef("feature", "main", PrStatus.Completed));
        this._jobs.GetById(Arg.Any<Guid>()).Returns(call => new ReviewJob(
            (Guid)call[0],
            this._clientId,
            "https://dev.azure.com/org",
            "project",
            "repo-1",
            7,
            1));

        this._sweeper = new CodeInsightSealSweeper(
            this._dbContext,
            this._sealer,
            gate,
            this._jobs,
            NullLogger<CodeInsightSealSweeper>.Instance,
            this._pullRequests);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }

        this._dataProtection?.Dispose();
        if (Directory.Exists(this._keysDirectory))
        {
            Directory.Delete(this._keysDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Seals_a_quiet_finished_pull_request_over_real_postgres()
    {
        var aggregateId = await this.SeedQuietPullRequestAsync(
            "repo-1",
            7,
            idleDays: 30,
            jobIds: [LaterJobId, EarlierJobId]);

        var sealedCount = await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7));

        Assert.Equal(1, sealedCount);
        await this._sealer.Received(1).SealAsync(
            Arg.Is<CodeInsightPullRequestKey>(key =>
                key.ClientId == this._clientId && key.RepositoryId == "repo-1" && key.PullRequestId == 7),
            "Completed",
            Arg.Any<CancellationToken>());

        await using var verification = new MeisterProPRDbContext(this._options);
        var aggregate = await verification.CodeInsightPullRequests.SingleAsync(row => row.Id == aggregateId);
        Assert.NotNull(aggregate.LastSealAttemptAt);
    }

    [Fact]
    public async Task Reads_the_provider_scope_from_the_smallest_job_id_of_the_aggregate()
    {
        await this.SeedQuietPullRequestAsync("repo-1", 8, idleDays: 30, jobIds: [LaterJobId, EarlierJobId]);

        await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7));

        this._jobs.Received(1).GetById(EarlierJobId);
        this._jobs.DidNotReceive().GetById(LaterJobId);
    }

    /// <summary>
    ///     Collects one finding per review job on the same pull request and backdates the aggregate's activity.
    /// </summary>
    private async Task<Guid> SeedQuietPullRequestAsync(
        string repositoryId,
        long pullRequestId,
        int idleDays,
        Guid[] jobIds)
    {
        var key = new CodeInsightPullRequestKey(this._clientId, repositoryId, pullRequestId);
        var reviewedAt = DateTimeOffset.UtcNow.AddDays(-idleDays);

        foreach (var jobId in jobIds)
        {
            var snapshot = new CodeInsightFindingSnapshot(
                0,
                "a.cs",
                10,
                CommentSeverity.Error,
                $"Finding of job {jobId:N}",
                "Baseline",
                null,
                null,
                false,
                ReviewCommentScopeRelation.OnChangedLine,
                null,
                $"thread-{jobId:N}",
                $"comment-{jobId:N}");

            await this._store.MaterialiseFindingsAsync(key, jobId, $"rev-{jobId:N}", reviewedAt, [snapshot]);
        }

        var aggregate = await this._dbContext.CodeInsightPullRequests
            .SingleAsync(candidate => candidate.ClientId == this._clientId
                                      && candidate.RepositoryId == repositoryId
                                      && candidate.PullRequestId == pullRequestId);
        aggregate.LastActivityAt = DateTimeOffset.UtcNow.AddDays(-idleDays);
        await this._dbContext.SaveChangesAsync();
        return aggregate.Id;
    }

    private ISecretProtectionCodec CreateCodec()
    {
        Directory.CreateDirectory(this._keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(this._keysDirectory));

        this._dataProtection = services.BuildServiceProvider();
        return new SecretProtectionCodec(this._dataProtection.GetRequiredService<IDataProtectionProvider>());
    }
}
