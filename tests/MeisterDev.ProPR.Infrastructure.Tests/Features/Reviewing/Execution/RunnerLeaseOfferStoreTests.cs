// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Which jobs a runner may be offered is decided entirely in SQL, because doing any of it in memory after
///     a LIMIT would drop whatever the limit cut off. For the fairness rule that would be a starvation bug,
///     and for the scope rule a cross-client leak. Both are asserted against the real database.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RunnerLeaseOfferStoreTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The installation's own tenant, whose runners are the shared pool.</summary>
    private static readonly Guid SystemTenantId = TenantCatalog.SystemTenantId;

    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private RunnerLeaseOfferStore _store = null!;
    private JobRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        await this._dbContext.ReviewRunners.ExecuteDeleteAsync();
        // Scoped to this class's own tenants. A blanket delete of clients and tenants also removes the
        // rows other suites in this collection seeded, which shows up as unrelated failures in a full run
        // and passes in isolation.
        await this._dbContext.Clients.Where(c => c.TenantId == TenantId || c.TenantId == OtherTenantId).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(t => t.Id == TenantId || t.Id == OtherTenantId).ExecuteDeleteAsync();
        this._repo = new JobRepository(this._dbContext, new TestDbContextFactory(this._options), NullLogger<JobRepository>.Instance);
        this._store = new RunnerLeaseOfferStore(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    // The scope boundary. A runner stamped for one client must not see another's work even when that other
    // job is older and would win every ordering rule.
    [Fact]
    public async Task ARunnerWithAStampedScope_IsOfferedNothingOutsideIt()
    {
        var inScope = await this.AddClientAsync(TenantId);
        var outOfScope = await this.AddClientAsync(TenantId);
        await this.AddPendingJobAsync(outOfScope, DateTimeOffset.UtcNow.AddMinutes(-30));
        var mine = await this.AddPendingJobAsync(inScope, DateTimeOffset.UtcNow);

        var offered = await this._store.GetOfferCandidatesAsync(TenantId, [inScope], [], 50);

        Assert.Equal([mine.Id], offered.Select(j => j.Id));
    }

    // An empty stamped scope is the unrestricted enrollment, and it still stops at the tenant.
    [Fact]
    public async Task AnEmptyScope_MeansEveryClientInTheTenantAndNoOther()
    {
        var mine = await this.AddClientAsync(TenantId);
        var theirs = await this.AddClientAsync(OtherTenantId);
        var ours = await this.AddPendingJobAsync(mine, DateTimeOffset.UtcNow);
        await this.AddPendingJobAsync(theirs, DateTimeOffset.UtcNow.AddMinutes(-30));

        var offered = await this._store.GetOfferCandidatesAsync(TenantId, [], [], 50);

        Assert.Equal([ours.Id], offered.Select(j => j.Id));
    }

    // A host enrolled in the System tenant belongs to the installation rather than to a customer, and is
    // offered every tenant's work. This is the only enrollment that crosses the tenant boundary, and it
    // allows an installation with many small tenants to run one or two runners in total.
    [Fact]
    public async Task ASystemTenantRunner_IsOfferedEveryTenantsWork()
    {
        var oneTenant = await this.AddClientAsync(TenantId);
        var another = await this.AddClientAsync(OtherTenantId);
        var older = await this.AddPendingJobAsync(another, DateTimeOffset.UtcNow.AddMinutes(-30));
        var newer = await this.AddPendingJobAsync(oneTenant, DateTimeOffset.UtcNow);

        var offered = await this._store.GetOfferCandidatesAsync(SystemTenantId, [], [], 50);

        // Each client's oldest job ranks first, so the two arrive in submission order across tenants.
        Assert.Equal([older.Id, newer.Id], offered.Select(j => j.Id));
    }

    // Shared does not mean unfiltered: a stamped scope still narrows a System runner to named clients,
    // and here that scope names a client of a tenant the runner does not belong to.
    [Fact]
    public async Task ASystemTenantRunner_StillHonoursItsStampedScope()
    {
        var wanted = await this.AddClientAsync(OtherTenantId);
        var ignored = await this.AddClientAsync(TenantId);
        await this.AddPendingJobAsync(ignored, DateTimeOffset.UtcNow.AddMinutes(-30));
        var mine = await this.AddPendingJobAsync(wanted, DateTimeOffset.UtcNow);

        var offered = await this._store.GetOfferCandidatesAsync(SystemTenantId, [wanted], [], 50);

        Assert.Equal([mine.Id], offered.Select(j => j.Id));
    }

    // The exception is the System tenant alone. An ordinary tenant's runner sees only its own work however
    // many tenants the installation has.
    [Fact]
    public async Task AnOrdinaryTenantRunner_NeverSeesAnotherTenantsWork()
    {
        var theirs = await this.AddClientAsync(OtherTenantId);
        await this.AddPendingJobAsync(theirs, DateTimeOffset.UtcNow.AddMinutes(-30));

        var offered = await this._store.GetOfferCandidatesAsync(TenantId, [], [], 50);

        Assert.Empty(offered);
    }

    // Fairness: one client with a deep queue must not hold the pool. Every client's oldest job comes before
    // any client's second-oldest, which oldest-first across the installation would get exactly backwards.
    [Fact]
    public async Task OrderingIsFairAcrossClients_RatherThanOldestFirstOverall()
    {
        var noisy = await this.AddClientAsync(TenantId);
        var quiet = await this.AddClientAsync(TenantId);
        var now = DateTimeOffset.UtcNow;
        var noisyFirst = await this.AddPendingJobAsync(noisy, now.AddMinutes(-50));
        var noisySecond = await this.AddPendingJobAsync(noisy, now.AddMinutes(-40));
        var noisyThird = await this.AddPendingJobAsync(noisy, now.AddMinutes(-30));
        var quietOnly = await this.AddPendingJobAsync(quiet, now.AddMinutes(-5));

        var offered = await this._store.GetOfferCandidatesAsync(TenantId, [], [], 50);

        // The quiet client's only job is second, ahead of the noisy client's backlog, despite being newest.
        Assert.Equal([noisyFirst.Id, quietOnly.Id, noisySecond.Id, noisyThird.Id], offered.Select(j => j.Id));
    }

    [Fact]
    public async Task AClientRequiringATag_IsOfferedOnlyToARunnerDeclaringIt()
    {
        var client = await this.AddClientAsync(TenantId, requiredTags: "gpu");
        var job = await this.AddPendingJobAsync(client, DateTimeOffset.UtcNow);

        Assert.Empty(await this._store.GetOfferCandidatesAsync(TenantId, [], [], 50));
        Assert.Empty(await this._store.GetOfferCandidatesAsync(TenantId, [], ["linux"], 50));
        Assert.Equal([job.Id], (await this._store.GetOfferCandidatesAsync(TenantId, [], ["gpu"], 50)).Select(j => j.Id));
        Assert.Equal([job.Id], (await this._store.GetOfferCandidatesAsync(TenantId, [], ["linux", "gpu"], 50)).Select(j => j.Id));
    }

    // Several required tags are a conjunction: declaring one of them is not enough. A runner that satisfied
    // a subset would be handed work its host cannot actually do.
    [Fact]
    public async Task EveryRequiredTagMustBeDeclared_NotJustOne()
    {
        var client = await this.AddClientAsync(TenantId, requiredTags: "gpu, large-disk");
        var job = await this.AddPendingJobAsync(client, DateTimeOffset.UtcNow);

        Assert.Empty(await this._store.GetOfferCandidatesAsync(TenantId, [], ["gpu"], 50));
        Assert.Equal([job.Id], (await this._store.GetOfferCandidatesAsync(TenantId, [], ["gpu", "large-disk"], 50)).Select(j => j.Id));
    }

    // Tags narrow within a scope and never widen it: declaring the tag does not buy a runner a client it
    // was not stamped for.
    [Fact]
    public async Task DeclaringATag_NeverWidensTheStampedScope()
    {
        var inScope = await this.AddClientAsync(TenantId);
        var outOfScope = await this.AddClientAsync(TenantId, requiredTags: "gpu");
        await this.AddPendingJobAsync(outOfScope, DateTimeOffset.UtcNow.AddMinutes(-30));

        var offered = await this._store.GetOfferCandidatesAsync(TenantId, [inScope], ["gpu"], 50);

        Assert.Empty(offered);
    }

    [Fact]
    public async Task AJobNoActiveRunnerCanTake_IsReportedAsUnroutable()
    {
        var client = await this.AddClientAsync(TenantId, requiredTags: "gpu");
        var job = await this.AddPendingJobAsync(client, DateTimeOffset.UtcNow.AddMinutes(-20));
        await this.AddRunnerAsync(TenantId, tags: ["linux"], lastSeen: DateTimeOffset.UtcNow);

        var unroutable = await this._store.GetUnroutableJobsAsync(DateTimeOffset.UtcNow.AddMinutes(-5), 50);

        var reported = Assert.Single(unroutable);
        Assert.Equal(job.Id, reported.JobId);
        Assert.Equal(["gpu"], reported.RequiredTags);
    }

    [Fact]
    public async Task AJobSomeActiveRunnerCanTake_IsNotUnroutable()
    {
        var client = await this.AddClientAsync(TenantId, requiredTags: "gpu");
        await this.AddPendingJobAsync(client, DateTimeOffset.UtcNow.AddMinutes(-20));
        await this.AddRunnerAsync(TenantId, tags: ["gpu"], lastSeen: DateTimeOffset.UtcNow);

        Assert.Empty(await this._store.GetUnroutableJobsAsync(DateTimeOffset.UtcNow.AddMinutes(-5), 50));
    }

    // A runner that stopped heartbeating cannot rescue a tagged job, so the job is unroutable again. The
    // opposite reading would leave an operator staring at a queue whose only runner died an hour ago.
    [Fact]
    public async Task ARunnerThatStoppedHeartbeating_NoLongerMakesAJobRoutable()
    {
        var client = await this.AddClientAsync(TenantId, requiredTags: "gpu");
        await this.AddPendingJobAsync(client, DateTimeOffset.UtcNow.AddMinutes(-20));
        await this.AddRunnerAsync(TenantId, tags: ["gpu"], lastSeen: DateTimeOffset.UtcNow.AddHours(-2));

        Assert.Single(await this._store.GetUnroutableJobsAsync(DateTimeOffset.UtcNow.AddMinutes(-5), 50));
    }

    // GetUnroutableJobsAsync has its own scope predicate, separate from the candidate query's. A runner
    // that declares the tag but was stamped for a different client cannot take this job, and reporting it
    // as routable would leave the queue looking merely busy while nothing could ever drain it.
    [Fact]
    public async Task ARunnerWithTheTagButTheWrongScope_DoesNotMakeAJobRoutable()
    {
        var needsGpu = await this.AddClientAsync(TenantId, requiredTags: "gpu");
        var somethingElse = await this.AddClientAsync(TenantId);
        await this.AddPendingJobAsync(needsGpu, DateTimeOffset.UtcNow.AddMinutes(-20));

        // Declares gpu, but is scoped to a client that is not the one waiting.
        await this.AddRunnerAsync(TenantId, tags: ["gpu"], lastSeen: DateTimeOffset.UtcNow, clientScope: [somethingElse]);

        var unroutable = await this._store.GetUnroutableJobsAsync(DateTimeOffset.UtcNow.AddMinutes(-5), 50);

        Assert.Equal([needsGpu], unroutable.Select(j => j.ClientId));
    }

    // A non-positive limit is a request for nothing. Passed through it reaches PostgreSQL's LIMIT and
    // raises an error, where the in-process claim path already answers with an empty result.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonPositiveLimit_ReturnsNothingRatherThanFailing(int limit)
    {
        var client = await this.AddClientAsync(TenantId);
        await this.AddPendingJobAsync(client, DateTimeOffset.UtcNow);

        Assert.Empty(await this._store.GetOfferCandidatesAsync(TenantId, [], [], limit));
        Assert.Empty(await this._store.GetUnroutableJobsAsync(DateTimeOffset.UtcNow.AddMinutes(-5), limit));
    }

    private async Task<Guid> AddClientAsync(Guid tenantId, string requiredTags = "")
    {
        if (!await this._dbContext.Tenants.AnyAsync(t => t.Id == tenantId))
        {
            this._dbContext.Tenants.Add(
                new TenantRecord
                {
                    Id = tenantId,
                    Slug = $"t{tenantId:N}",
                    DisplayName = $"tenant-{tenantId:N}",
                });
        }

        var clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = tenantId,
                DisplayName = $"client-{clientId:N}",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                RequiredRunnerTags = requiredTags,
            });

        await this._dbContext.SaveChangesAsync();
        return clientId;
    }

    private async Task<ReviewJob> AddPendingJobAsync(Guid clientId, DateTimeOffset submittedAt)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            Random.Shared.Next(1, 100_000),
            1);
        await this._repo.AddAsync(job);

        // submitted_at is set by the entity on construction, and fairness is entirely about its order, so
        // the test writes the value it needs rather than sleeping between inserts.
        await this._dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE review_jobs SET submitted_at = {0} WHERE id = {1}",
            submittedAt,
            job.Id);

        return job;
    }

    private async Task AddRunnerAsync(Guid tenantId, string[] tags, DateTimeOffset lastSeen, Guid[]? clientScope = null)
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(),
            tenantId,
            "runner-01",
            clientScope ?? [],
            1,
            "hashed:secret",
            $"LOOKUP{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);
        runner.DeclareTags(tags);
        runner.MarkSeen(lastSeen);
        this._dbContext.ReviewRunners.Add(runner);
        await this._dbContext.SaveChangesAsync();
    }
}
