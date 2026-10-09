// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.TestSupport;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;
using PostgresContainerFixture = MeisterDev.ProPR.TestSupport.PostgresContainerFixture;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Api.Tests.Features.Crawling;

[Collection("RepositoryTargetPostgresIntegration")]
public sealed class ClientReviewTargetsPostgresTests(PostgresContainerFixture fixture)
{
    [Xunit.Theory]
    [InlineData("revision", ReviewTargetLifecycle.Disabled)]
    [InlineData("revision", ReviewTargetLifecycle.Removed)]
    [InlineData("revision", ReviewTargetLifecycle.Enabled)]
    [InlineData("lazy", ReviewTargetLifecycle.Disabled)]
    [InlineData("lazy", ReviewTargetLifecycle.Enabled)]
    [InlineData("retry", ReviewTargetLifecycle.Removed)]
    [InlineData("maintenance", ReviewTargetLifecycle.Disabled)]
    [InlineData("batch", ReviewTargetLifecycle.Disabled)]
    public async Task ProviderPreparation_TargetEditCompletesBeforeReadReturnsAndPreservesAcceptedJob(string read, ReviewTargetLifecycle lifecycle)
    {
        Assert.True(fixture.IsAvailable, "The isolated PostgreSQL fixture is required for admission regressions.");
        var options = this.LockTestOptions(out _);
        await using var accepting = new MeisterProPRDbContext(options);
        await using var editing = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        accepting.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Preparation test", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await accepting.SaveChangesAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? running = null;
        Task<bool>? mutation = null;
        try
        {
            var targets = new CrawlConfigurationRepository(accepting);
            var target = await targets.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://prepare.example.test", "owner", "native", "repo");
            await accepting.CrawlConfigurations.Where(config => config.Id == target.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(config => config.IsActive, true));
            var accepted = new ReviewJob(Guid.NewGuid(), clientId, target.ProviderScopePath, "owner", "native", read == "batch" ? 99 : 7, 1);
            accepted.SetReviewRevision(new ReviewRevision("old", "base", null, "old", "base...old"));
            accepting.ReviewJobs.Add(accepted);
            await accepting.SaveChangesAsync();
            var host = new ProviderHostRef(ScmProvider.GitHub, target.ProviderScopePath);
            var repository = new RepositoryRef(host, "native", "owner", "owner/repo", "repo");
            var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7);
            accepted.SetProviderReviewContext(
                read == "batch"
                    ? new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "99", 99)
                    : review);
            await accepting.SaveChangesAsync();
            var revision = new ReviewRevision("new", "base", null, "new", "base...new");
            var jobs = new JobRepository(accepting, Substitute.For<IDbContextFactory<MeisterProPRDbContext>>(), NullLogger<JobRepository>.Instance);
            var statuses = Substitute.For<IReviewerThreadStatusFetcher>();
            var memory = Substitute.For<IThreadMemoryService>();
            var scans = Substitute.For<IReviewPrScanRepository>();
            var scan = new ReviewPrScan(Guid.NewGuid(), clientId, target.ProviderScopePath, "owner", "native", 7, read == "batch" ? "old" : "new");
            scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scan.Id, ThreadId = "17", LastSeenStatus = "Active" });
            scans.GetAsync(clientId, Arg.Any<string>(), "owner", "native", 7, Arg.Any<CancellationToken>()).Returns(scan);
            if (read == "batch")
            {
                scans.GetAsync(clientId, Arg.Any<string>(), "owner", "native", 8, Arg.Any<CancellationToken>()).Returns(
                    new ReviewPrScan(Guid.NewGuid(), clientId, target.ProviderScopePath, "owner", "native", 8, "new"));
            }

            var reads = 0;
            statuses.GetReviewerThreadStatusesAsync(
                    Arg.Any<string>(), "owner", "native", Arg.Any<int>(),
                    Arg.Any<ThreadOwnershipResolver>(), clientId, Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (read == "batch" && call.Arg<int>() == 7)
                    {
                        return (IReadOnlyList<PrThreadStatusEntry>)[];
                    }

                    if (++reads == 1 && read == "retry")
                    {
                        throw new InvalidOperationException("Synthetic first-read failure.");
                    }

                    entered.TrySetResult();
                    await proceed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    return (IReadOnlyList<PrThreadStatusEntry>)[new("17", "Fixed", "/file.cs", "Resolved", 1)];
                });
            var synchronization = new PullRequestSynchronizationService(
                MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
                jobs, NullLogger<PullRequestSynchronizationService>.Instance,
                threadStatusFetcher: read is "lazy" or "retry" or "maintenance" or "batch" ? statuses : null,
                threadMemoryService: read is "retry" or "maintenance" ? memory : null, prScanRepository: scans);
            if (read == "revision")
            {
                var query = Substitute.For<ICodeReviewQueryService>();
                query.GetReviewAsync(clientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>()).Returns(
                    new ReviewDiscoveryItemDto(ScmProvider.GitHub, repository, review, CodeReviewState.Open, null, null, "Review", null, "feature", "main"));
                query.GetLatestRevisionAsync(clientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>()).Returns(async _ =>
                {
                    entered.TrySetResult();
                    await proceed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    return (ReviewRevision?)revision;
                });
                var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
                registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
                registry.GetCodeReviewQueryService(ScmProvider.GitHub).Returns(query);
                running = new SubmitReviewByCoordinatesHandler(
                    targets, Substitute.For<IWebhookConfigurationRepository>(), registry,
                    synchronization, NullLogger<SubmitReviewByCoordinatesHandler>.Instance).HandleAsync(
                    new SubmitReviewByCoordinatesCommand(clientId, target.ProviderScopePath, "owner", "native", 7, true));
            }
            else
            {
                var discovery = Substitute.For<IAssignedReviewDiscoveryService>();
                discovery.ListAssignedOpenReviewsAsync(Arg.Any<CrawlConfigurationDto>(), Arg.Any<CancellationToken>()).Returns(
                    read == "batch"
                        ?
                        [
                            new AssignedCodeReviewRef(host, repository, review, 2, "Review", "repo", "feature", "main", revision),
                            new AssignedCodeReviewRef(
                                host, repository, new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "8", 8), 2,
                                "Review", "repo", "feature", "main", revision)
                        ]
                        : [new AssignedCodeReviewRef(host, repository, review, 2, "Review", "repo", "feature", "main", revision)]);
                var lifecycleFetcher = Substitute.For<IPrStatusFetcher>();
                lifecycleFetcher.GetStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), clientId, Arg.Any<CancellationToken>())
                    .Returns(PrStatus.Active);
                var policies = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
                policies.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
                running = new PrCrawlService(
                    targets, discovery, jobs, lifecycleFetcher, NullLogger<PrCrawlService>.Instance,
                    pullRequestSynchronizationService: synchronization, providerRegistry: policies).CrawlAsync();
            }

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (read == "batch")
            {
                Assert.True(await editing.ReviewJobs.AnyAsync(job => job.ClientId == clientId && job.PullRequestId == 7 && job.IterationId == 2));
            }

            var mutations = new CrawlConfigurationRepository(editing);
            mutation = lifecycle == ReviewTargetLifecycle.Enabled
                ? mutations.UpdateReviewTargetPolicyAsync(target, clientId, [], ["release/*"])
                : mutations.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, lifecycle);
            Assert.True(await mutation.WaitAsync(TimeSpan.FromSeconds(2)));
            proceed.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            var stored = await editing.ReviewJobs.AsNoTracking().Where(job => job.ClientId == clientId).ToListAsync();
            if (read == "batch")
            {
                Assert.Equal(2, stored.Count);
                Assert.Contains(stored, job => job.Id == accepted.Id && job.Status == JobStatus.Pending);
                Assert.Contains(stored, job => job.PullRequestId == 7 && job.IterationId == 2 && job.Status == JobStatus.Pending);
                Assert.DoesNotContain(stored, job => job.PullRequestId == 8);
            }
            else
            {
                Assert.Equal(accepted.Id, Assert.Single(stored).Id);
                Assert.Equal(JobStatus.Pending, stored[0].Status);
            }

            if (read == "maintenance")
            {
                await memory.Received(1).HandleThreadResolvedAsync(
                    Arg.Any<MeisterDev.ProPR.Domain.Events.ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
            }

            if (read == "retry")
            {
                Assert.Equal(2, reads);
            }
        }
        finally
        {
            proceed.TrySetResult();
            if (running is not null)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (mutation is not null)
            {
                await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await accepting.ReviewJobs.Where(job => job.ClientId == clientId).ExecuteDeleteAsync();
            await accepting.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData(ReviewTargetLifecycle.Enabled)]
    [InlineData(ReviewTargetLifecycle.Disabled)]
    [InlineData(ReviewTargetLifecycle.Removed)]
    public async Task LegacyDelete_RejectsManagedCanonicalTargetsAndPreservesSavedIdentity(ReviewTargetLifecycle lifecycle)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var context = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        context.Clients.Add(
            new ClientRecord
                { Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Deletion test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(context);
            var target = await repository.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub, "https://delete.example.test", "team", "native", "repo", targetBranchPatterns: ["main"]);
            if (lifecycle != ReviewTargetLifecycle.Enabled)
            {
                Assert.True(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, lifecycle));
            }

            var before = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.False(await repository.DeleteAsync(target.Id, clientId));
            var after = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(before.ReviewTargetLifecycle, after.ReviewTargetLifecycle);
            Assert.Equal(before.ReviewTargetRevision, after.ReviewTargetRevision);
            Assert.Equal(before.RepoFilters[0].CanonicalSourceRef, after.RepoFilters[0].CanonicalSourceRef);
            Assert.Equal(before.RepoFilters[0].TargetBranchPatterns, after.RepoFilters[0].TargetBranchPatterns);
            var generic = await repository.AddAsync(clientId, ScmProvider.GitHub, "https://delete.example.test", "generic", 60);
            Assert.True(await repository.DeleteAsync(generic.Id, clientId));
            Assert.Null(await repository.GetByIdAsync(generic.Id));
        }
        finally
        {
            await context.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("multiple")]
    [InlineData("noncanonical")]
    public async Task Restoration_RejectsMalformedInternalFilterShapesWithoutChangingSavedTarget(string shape)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var context = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        context.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Restoration shape test", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await context.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(context);
            var target = await repository.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub, "https://restore.example.test", "team", "native", "repo", targetBranchPatterns: ["main"]);
            Assert.True(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, ReviewTargetLifecycle.Removed));
            var removed = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            var malformed = removed with
            {
                RepoFilters = shape switch
                {
                    "none" => [], "multiple" => [removed.RepoFilters[0], removed.RepoFilters[0]],
                    _ => [removed.RepoFilters[0] with { CanonicalSourceRef = null }]
                }
            };
            Assert.False(await repository.RestoreReviewTargetAsync(malformed, clientId, ["replacement"]));
            var saved = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(removed.ReviewTargetRevision, saved.ReviewTargetRevision);
            Assert.Equal(ReviewTargetLifecycle.Removed, saved.ReviewTargetLifecycle);
            Assert.Equal(removed.RepoFilters[0].CanonicalSourceRef, saved.RepoFilters[0].CanonicalSourceRef);
            Assert.Equal(new[] { "main" }, saved.RepoFilters[0].TargetBranchPatterns);
        }
        finally
        {
            await context.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    public async Task Restore_RejectsInvalidDestinationPatternsWithoutChangingRemovedTarget(string invalidPolicy)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var context = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        try
        {
            context.Clients.Add(
                new ClientRecord
                {
                    Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Restoration policy test", IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await context.SaveChangesAsync();
            var repository = new CrawlConfigurationRepository(context);
            var target = await repository.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub, "https://restore-policy.example.test", "team", "native", "repo", targetBranchPatterns: ["main"]);
            Assert.True(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, ReviewTargetLifecycle.Removed));
            var removed = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            string[] patterns = invalidPolicy switch
            {
                "blank" => [" "], "duplicate" => ["main", "MAIN"], _ => [new string('a', 513)]
            };

            Assert.False(await repository.RestoreReviewTargetAsync(removed, clientId, patterns));

            var saved = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(removed.Id, saved.Id);
            Assert.Equal(removed.ReviewTargetRevision, saved.ReviewTargetRevision);
            Assert.Equal(ReviewTargetLifecycle.Removed, saved.ReviewTargetLifecycle);
            Assert.False(saved.IsActive);
            Assert.Equal(removed.RepoFilters[0].CanonicalSourceRef, saved.RepoFilters[0].CanonicalSourceRef);
            Assert.Equal(new[] { "main" }, saved.RepoFilters[0].TargetBranchPatterns);
        }
        finally
        {
            await context.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Lifecycle_RejectsUndefinedInternalValueWithoutChangingSavedTarget()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var context = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        context.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Lifecycle value test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
        await context.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(context);
            var target = await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://lifecycle.example.test", "team", "native", "repo");
            Assert.False(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, (ReviewTargetLifecycle)99));
            var saved = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(ReviewTargetLifecycle.Enabled, saved.ReviewTargetLifecycle);
            Assert.Equal(1, saved.ReviewTargetRevision);
        }
        finally
        {
            await context.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ManagementPage_SnapshotVersionIsStableAcrossPagesAndChangesForEqualCountReplacementAndMetadataWrites()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var reader = new MeisterProPRDbContext(options);
        await using var writer = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        writer.Clients.Add(
            new ClientRecord
                { Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Version test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
        await writer.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(writer);
            for (var index = 0; index < 101; index++)
            {
                await repository.AddReviewTargetAsync(
                    clientId, ScmProvider.GitHub, "https://version.example.test", "team", $"native-{index}", $"repo-{index:D3}");
            }

            var reading = new CrawlConfigurationRepository(reader);
            var first = await reading.GetManagementTargetPageAsync(clientId, null, null, null, 1, 100);
            var last = await reading.GetManagementTargetPageAsync(clientId, null, null, null, 2, 100);
            var version = SnapshotVersion(first);
            Assert.Matches("^[0-9a-f]{64}$", version);
            Assert.Equal(version, SnapshotVersion(last));
            Assert.True(await repository.ChangeReviewTargetLifecycleAsync(first.Items[0].Id, clientId, 1, ReviewTargetLifecycle.Removed));
            await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://version.example.test", "team", "new-native", "zzz-new");
            var replaced = await reading.GetManagementTargetPageAsync(clientId, null, null, null, 2, 100);
            Assert.Equal(first.TotalCount, replaced.TotalCount);
            Assert.NotEqual(version, SnapshotVersion(replaced));
            await writer.CrawlRepoFilters.Where(f => f.CrawlConfigurationId == last.Items[0].Id)
                .ExecuteUpdateAsync(set => set.SetProperty(f => f.RepositoryName, "renamed"));
            var renamed = await reading.GetManagementTargetPageAsync(clientId, null, null, null, 1, 100);
            Assert.NotEqual(SnapshotVersion(replaced), SnapshotVersion(renamed));
            Assert.Equal(SnapshotVersion(renamed), SnapshotVersion(await reading.GetManagementTargetPageAsync(clientId, null, null, null, 2, 100)));
            var current = (await repository.GetReviewTargetPolicySnapshotAsync(last.Items[0].Id, clientId))!;
            Assert.True(await repository.UpdateReviewTargetPolicyAsync(current, clientId, [], ["main"]));
            var policy = await reading.GetManagementTargetPageAsync(clientId, null, null, null, 1, 100);
            Assert.NotEqual(SnapshotVersion(renamed), SnapshotVersion(policy));
            current = (await repository.GetReviewTargetPolicySnapshotAsync(last.Items[0].Id, clientId))!;
            Assert.True(await repository.ChangeReviewTargetLifecycleAsync(current.Id, clientId, current.ReviewTargetRevision, ReviewTargetLifecycle.Disabled));
            Assert.NotEqual(SnapshotVersion(policy), SnapshotVersion(await reading.GetManagementTargetPageAsync(clientId, null, null, null, 1, 100)));
        }
        finally
        {
            await writer.Clients.Where(c => c.Id == clientId).ExecuteDeleteAsync();
        }
    }

    private static string SnapshotVersion(CrawlConfigurationPageDto page) => page.SnapshotVersion;

    private DbContextOptions<MeisterProPRDbContext> LockTestOptions(out string applicationName)
    {
        applicationName = $"tlock-{Guid.NewGuid():N}";
        var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = applicationName };
        return new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(settings.ConnectionString, o => o.UseVector()).Options;
    }

    private async Task WaitForPendingAdmissionAsync(string applicationName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE a.application_name = @name AND l.locktype = 'advisory' AND NOT l.granted)",
            observer);
        command.Parameters.AddWithValue("name", $"{applicationName}:review-target-admission");
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!)
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("filters")]
    [InlineData("activation")]
    public async Task LegacyTargetWrite_WaitsForEarlierAdmissionBeforeChangingRevisionOrPolicy(string operation)
    {
        fixture.SkipIfUnavailable();
        var options = this.LockTestOptions(out var applicationName);
        await using var admitting = new MeisterProPRDbContext(options);
        await using var editing = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        admitting.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Mutation lock test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
        await admitting.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(admitting);
            var target = await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://mutate.example.test", "team", "native", "repo");
            var lease = await repository.AcquireReviewTargetAdmissionAsync(clientId);
            var writer = new CrawlConfigurationRepository(editing);
            var filters = new[] { target.RepoFilters[0] with { TargetBranchPatterns = ["release/*"] } };
            var write = operation switch
            {
                "settings" => writer.UpdateAsync(target.Id, 900, null, clientId, repoFilters: filters),
                "filters" => writer.UpdateRepoFiltersAsync(target.Id, filters),
                _ => writer.SetActiveAsync(target.Id, clientId, true),
            };
            try
            {
                await this.WaitForPendingAdmissionAsync(applicationName);
                Assert.False(write.IsCompleted);
                Assert.Equal(1, (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!.ReviewTargetRevision);
            }
            finally
            {
                await lease.DisposeAsync();
                await write.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.True(await write);
            Assert.Equal(2, (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!.ReviewTargetRevision);
        }
        finally
        {
            await admitting.Clients.Where(c => c.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("filters")]
    [InlineData("activation")]
    public async Task LegacyTargetWrite_AdvancesCurrentRevisionAndRejectsStaleLifecycleConfirmation(string operation)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var stale = new MeisterProPRDbContext(options);
        await using var writer = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        writer.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Legacy revision test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
        await writer.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(writer);
            var target = await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://revision.example.test", "team", "native", "repo");
            await stale.CrawlConfigurations.Include(c => c.RepoFilters).SingleAsync(c => c.Id == target.Id);
            Assert.True(await repository.UpdateReviewTargetPolicyAsync(target, clientId, [], ["main"]));
            var alternate = new CrawlConfigurationRepository(stale);
            var filters = new[] { target.RepoFilters[0] with { TargetBranchPatterns = ["release/*"] } };
            Assert.True(
                operation switch
                {
                    "settings" => await alternate.UpdateAsync(target.Id, 900, null, clientId, repoFilters: filters),
                    "filters" => await alternate.UpdateRepoFiltersAsync(target.Id, filters),
                    _ => await alternate.SetActiveAsync(target.Id, clientId, true),
                });
            var saved = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(3, saved.ReviewTargetRevision);
            Assert.False(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 2, ReviewTargetLifecycle.Disabled));
        }
        finally
        {
            await writer.Clients.Where(c => c.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("filters")]
    [InlineData("delete")]
    public async Task RemovedTarget_LegacyCanonicalMutationCannotEraseSavedExclusion(string operation)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var stale = new MeisterProPRDbContext(options);
        await using var writer = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        writer.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Removed exclusion test", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await writer.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(writer);
            var target = await repository.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub, "https://removed.example.test", "team", "native", "repo", targetBranchPatterns: ["main"]);
            await stale.CrawlConfigurations.Include(c => c.RepoFilters).SingleAsync(c => c.Id == target.Id);
            Assert.True(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, ReviewTargetLifecycle.Removed));
            var filters = new[] { target.RepoFilters[0] with { CanonicalSourceRef = new("GitHub", "replacement"), TargetBranchPatterns = ["release/*"] } };
            var alternate = new CrawlConfigurationRepository(stale);
            Assert.False(
                operation switch
                {
                    "settings" => await alternate.UpdateAsync(target.Id, null, null, clientId, repoFilters: filters),
                    "filters" => await alternate.UpdateRepoFiltersAsync(target.Id, filters),
                    _ => await alternate.DeleteAsync(target.Id, clientId),
                });
            var saved = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(ReviewTargetLifecycle.Removed, saved.ReviewTargetLifecycle);
            Assert.Equal("native", saved.RepoFilters[0].CanonicalSourceRef!.Value);
            Assert.Equal(["main"], saved.RepoFilters[0].TargetBranchPatterns);
            Assert.Equal(2, saved.ReviewTargetRevision);
            Assert.Contains(
                await repository.GetManagementTargetsAsync(clientId),
                c => c.Id == target.Id && c.ReviewTargetLifecycle == ReviewTargetLifecycle.Removed && c.RepoFilters[0].CanonicalSourceRef!.Value == "native");
            await repository.AddAsync(clientId, ScmProvider.GitHub, target.ProviderScopePath, target.ProviderProjectKey, 60);
            var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
            registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
            var synchronization = Substitute.For<IPullRequestSynchronizationService>();
            var handler = new SubmitReviewByCoordinatesHandler(
                repository, Substitute.For<IWebhookConfigurationRepository>(), registry, synchronization,
                NullLogger<SubmitReviewByCoordinatesHandler>.Instance);
            Assert.Equal(
                SubmitReviewByCoordinatesOutcome.NotAuthorized,
                (await handler.HandleAsync(new(clientId, target.ProviderScopePath, target.ProviderProjectKey, "native", 7, true))).Outcome);
            await synchronization.DidNotReceive().SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
            registry.DidNotReceive().GetCodeReviewQueryService(Arg.Any<ScmProvider>());
        }
        finally
        {
            await writer.Clients.Where(c => c.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData(false, ReviewTargetLifecycle.Disabled)]
    [InlineData(true, ReviewTargetLifecycle.Disabled)]
    [InlineData(false, ReviewTargetLifecycle.Removed)]
    [InlineData(true, ReviewTargetLifecycle.Removed)]
    public async Task Activation_RejectsStaleTrackedEnabledStateAfterLifecycleReturns(bool updateSettings, ReviewTargetLifecycle lifecycle)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var stale = new MeisterProPRDbContext(options);
        await using var writer = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        stale.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Activation test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
        await stale.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(stale);
            var target = await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://activation.example.test", "team", "native", "repo");
            var tracked = await stale.CrawlConfigurations.SingleAsync(c => c.Id == target.Id);
            var originalInterval = tracked.CrawlIntervalSeconds;
            Assert.Equal(ReviewTargetLifecycle.Enabled, tracked.ReviewTargetLifecycle);
            Assert.True(await new CrawlConfigurationRepository(writer).ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, lifecycle));
            Assert.Equal(ReviewTargetLifecycle.Enabled, tracked.ReviewTargetLifecycle);
            var changed = updateSettings
                ? await repository.UpdateAsync(target.Id, 900, true, clientId)
                : await repository.SetActiveAsync(target.Id, clientId, true);
            Assert.False(changed);
            var persisted = await writer.CrawlConfigurations.AsNoTracking().SingleAsync(c => c.Id == target.Id);
            Assert.Equal(lifecycle, persisted.ReviewTargetLifecycle);
            Assert.False(persisted.IsActive);
            Assert.Equal(originalInterval, persisted.CrawlIntervalSeconds);
        }
        finally
        {
            await writer.Clients.Where(c => c.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Admission_HeldAndWaitingLeasesDoNotStarveSingleConnectionOrmPool()
    {
        fixture.SkipIfUnavailable();
        var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { MaxPoolSize = 1, MinPoolSize = 0, Timeout = 2, ApplicationName = $"small-admission-{Guid.NewGuid():N}" };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await using var waitingConnection = new NpgsqlConnection(settings.ConnectionString);
        await using var context =
            new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connection, o => o.UseVector()).Options);
        await using var waitingContext = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(waitingConnection, o => o.UseVector()).Options);
        await context.Database.ExecuteSqlRawAsync("SELECT 1");
        var clientId = Guid.NewGuid();
        await using var lease = await new CrawlConfigurationRepository(context).AcquireReviewTargetAdmissionAsync(clientId);
        using var waitingCancellation = new CancellationTokenSource();
        var waiting = new CrawlConfigurationRepository(waitingContext).AcquireReviewTargetAdmissionAsync(clientId, waitingCancellation.Token);
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await context.Database.ExecuteSqlRawAsync("SELECT 1", bound.Token);
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
    }

    [Fact]
    public async Task Admission_LockConnectionsRemainBoundedWhenOrmPoolingIsDisabled()
    {
        fixture.SkipIfUnavailable();
        var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Pooling = false, ApplicationName = $"unpooled-admission-{Guid.NewGuid():N}" };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await using var context = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connection, o => o.UseVector()).Options);
        var repository = new CrawlConfigurationRepository(context);
        var leases = new List<IAsyncDisposable>();
        try
        {
            for (var index = 0; index < 16; index++)
            {
                leases.Add(await repository.AcquireReviewTargetAdmissionAsync(Guid.NewGuid()));
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using var unexpected = await repository.AcquireReviewTargetAdmissionAsync(Guid.NewGuid(), cancellation.Token);
            });
            await context.Database.ExecuteSqlRawAsync("SELECT 1");
        }
        finally
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
        }

        await using var recovered = await repository.AcquireReviewTargetAdmissionAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task ManagementPage_CountAndItemsRemainInOneSnapshotDuringConcurrentInsertion()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var writer = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            writer.Clients.Add(
                new ClientRecord
                {
                    Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Snapshot test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
                });
            await writer.SaveChangesAsync();
            var repository = new CrawlConfigurationRepository(writer);
            await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://snapshot.example.test", "team", "first", "first");
            var interceptor = new CountReadBarrier(entered, proceed);
            var readingOptions = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .AddInterceptors(interceptor).Options;
            await using var reading = new MeisterProPRDbContext(readingOptions);
            var page = new CrawlConfigurationRepository(reading).GetManagementTargetPageAsync(clientId, null, null, null, 1, 25);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://snapshot.example.test", "team", "second", "second");
            proceed.TrySetResult();
            var snapshot = await page.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, snapshot.TotalCount);
            Assert.Single(snapshot.Items);
            Assert.Equal("first", snapshot.Items[0].RepoFilters[0].RepositoryName);
            Assert.Equal(2, (await repository.GetManagementTargetPageAsync(clientId, null, null, null, 1, 25)).TotalCount);
        }
        finally
        {
            proceed.TrySetResult();
            await writer.Clients.Where(c => c.Id == clientId).ExecuteDeleteAsync();
        }
    }

    private sealed class CountReadBarrier(TaskCompletionSource entered, TaskCompletionSource proceed) : DbCommandInterceptor
    {
        private bool _countRead;

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command, CommandExecutedEventData eventData, System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_countRead && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase))
            {
                _countRead = true;
                entered.TrySetResult();
                await proceed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task Lifecycle_DisableSerializesWithCustomerJobPersistenceAndPreservesAcceptedJob()
    {
        Assert.True(fixture.IsAvailable, "The isolated PostgreSQL fixture is required for admission regressions.");
        var options = this.LockTestOptions(out var applicationName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new JobPersistenceBarrier(entered, proceed);
        var admittingOptions = new DbContextOptionsBuilder<MeisterProPRDbContext>(options).AddInterceptors(barrier).Options;
        await using var admissionContext = new MeisterProPRDbContext(admittingOptions);
        await using var lifecycleContext = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        admissionContext.Clients.Add(
            new ClientRecord
                { Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Admission test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
        await admissionContext.SaveChangesAsync();
        try
        {
            var targets = new CrawlConfigurationRepository(admissionContext);
            var target = await targets.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "native", "repo");
            var sourceId = Guid.NewGuid();
            admissionContext.ProCursorKnowledgeSources.Add(
                new ProCursorKnowledgeSource(
                    sourceId, clientId, "Saved source",
                    ProCursorSourceKind.Repository, target.ProviderScopePath, "owner", "native", "main", null, true, "none"));
            var completedId = Guid.NewGuid();
            var completed = new ReviewJob(completedId, clientId, target.ProviderScopePath, "owner", "native", 6, 1)
                { Status = JobStatus.Completed, CompletedAt = DateTimeOffset.UtcNow };
            admissionContext.ReviewJobs.Add(completed);
            await admissionContext.SaveChangesAsync();
            Assert.True(await targets.UpdateSourceScopeAsync(target.Id, ProCursorSourceScopeMode.SelectedSources, [sourceId]));
            target = (await targets.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            var host = new ProviderHostRef(ScmProvider.GitHub, target.ProviderScopePath);
            var repository = new RepositoryRef(host, "native", "owner", "owner/repo", "repo");
            var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7);
            var query = Substitute.For<ICodeReviewQueryService>();
            query.GetReviewAsync(clientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>()).Returns(
                new ReviewDiscoveryItemDto(
                    ScmProvider.GitHub, repository, review, CodeReviewState.Open,
                    new ReviewRevision("head", "base", null, "head", "base...head"), null, "Test review", null, "feature", "main"));
            var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
            registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
            registry.GetCodeReviewQueryService(ScmProvider.GitHub).Returns(query);
            var jobs = new JobRepository(admissionContext, Substitute.For<IDbContextFactory<MeisterProPRDbContext>>(), NullLogger<JobRepository>.Instance);
            var synchronization = new PullRequestSynchronizationService(
                MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance);
            var handler = new SubmitReviewByCoordinatesHandler(
                targets, Substitute.For<IWebhookConfigurationRepository>(), registry,
                synchronization, NullLogger<SubmitReviewByCoordinatesHandler>.Instance);
            var command = new SubmitReviewByCoordinatesCommand(clientId, target.ProviderScopePath, "owner", "native", 7, true);
            barrier.Enabled = true;
            var submitting = handler.HandleAsync(command);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disabling = new CrawlConfigurationRepository(lifecycleContext).ChangeReviewTargetLifecycleAsync(
                target.Id, clientId, target.ReviewTargetRevision, ReviewTargetLifecycle.Disabled);
            await this.WaitForPendingAdmissionAsync(applicationName);
            Assert.False(disabling.IsCompleted);
            proceed.TrySetResult();
            var submitted = await submitting;
            Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, submitted.Outcome);
            var jobId = submitted.JobId!.Value;
            Assert.True(await disabling.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, (await handler.HandleAsync(command)).Outcome);
            Assert.True(await lifecycleContext.ReviewJobs.AnyAsync(job => job.Id == jobId));
            var disabled = (await targets.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.True(
                await new CrawlConfigurationRepository(lifecycleContext).ChangeReviewTargetLifecycleAsync(
                    target.Id, clientId, disabled.ReviewTargetRevision, ReviewTargetLifecycle.Removed));
            Assert.True(await lifecycleContext.ReviewJobs.AnyAsync(job => job.Id == jobId));
            Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, (await handler.HandleAsync(command)).Outcome);
            var persisted = await lifecycleContext.ReviewJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
            Assert.Equal(JobStatus.Pending, persisted.Status);
            Assert.Equal("main", persisted.PrTargetBranch);
            Assert.Equal(ProCursorSourceScopeMode.SelectedSources, persisted.ProCursorSourceScopeMode);
            Assert.True(
                await lifecycleContext.ReviewJobProCursorSourceScopes.AnyAsync(scope => scope.ReviewJobId == jobId && scope.ProCursorSourceId == sourceId));
            Assert.True(await lifecycleContext.ReviewJobs.AnyAsync(job => job.Id == completedId && job.Status == JobStatus.Completed));
            Assert.True(await lifecycleContext.ProCursorKnowledgeSources.AnyAsync(source => source.Id == sourceId));
        }
        finally
        {
            proceed.TrySetResult();
            await admissionContext.ReviewJobs.Where(job => job.ClientId == clientId).ExecuteDeleteAsync();
            await admissionContext.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    private sealed class JobPersistenceBarrier(TaskCompletionSource entered, TaskCompletionSource proceed) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (this.Enabled && eventData.Context!.ChangeTracker.Entries<ReviewJob>().Any(entry => entry.State == EntityState.Added))
            {
                this.Enabled = false;
                entered.TrySetResult();
                await proceed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task Admission_CancelledWaitAndDisposedLeaseReleaseDatabaseLock()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var firstContext = new MeisterProPRDbContext(options);
        await using var secondContext = new MeisterProPRDbContext(options);
        var first = new CrawlConfigurationRepository(firstContext);
        var second = new CrawlConfigurationRepository(secondContext);
        var clientId = Guid.NewGuid();
        var held = await first.AcquireReviewTargetAdmissionAsync(clientId);
        try
        {
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireReviewTargetAdmissionAsync(clientId, cancelled.Token));
        }
        finally
        {
            await held.DisposeAsync();
        }

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var next = await second.AcquireReviewTargetAdmissionAsync(clientId, bound.Token))
        {
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var failedAdmission = await first.AcquireReviewTargetAdmissionAsync(clientId, bound.Token);
            throw new InvalidOperationException("Admission failed before queue persistence.");
        });
        await using var afterError = await second.AcquireReviewTargetAdmissionAsync(clientId, bound.Token);
    }

    [Fact]
    public async Task ManagementPage_FiltersBeforeCountAndPagingAndOrdersByNameThenIdentity()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var context = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        context.Clients.Add(
            new ClientRecord
                { Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Paging test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(context);
            for (var index = 29; index >= 0; index--)
            {
                await repository.AddReviewTargetAsync(
                    clientId, ScmProvider.GitHub, "https://saved.example.test", "Project", $"native-{index}", $"repository-{index:D2}");
            }

            var first = await repository.GetManagementTargetPageAsync(clientId, "SAVED.example", ScmProvider.GitHub, ReviewTargetLifecycle.Enabled, 1, 25);
            var next = await repository.GetManagementTargetPageAsync(clientId, "SAVED.example", ScmProvider.GitHub, ReviewTargetLifecycle.Enabled, 2, 25);
            Assert.Equal(30, first.TotalCount);
            Assert.Equal(25, first.Items.Count);
            Assert.Equal("repository-00", first.Items[0].RepoFilters[0].RepositoryName);
            Assert.Equal(5, next.Items.Count);
            Assert.Equal("repository-25", next.Items[0].RepoFilters[0].RepositoryName);
            var match = await repository.GetManagementTargetPageAsync(clientId, "repository-29", null, null, 1, 25);
            Assert.Equal(1, match.TotalCount);
            Assert.Single(match.Items);
            var target = match.Items[0];
            Assert.True(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, ReviewTargetLifecycle.Disabled));
            Assert.Equal(1, (await repository.GetManagementTargetPageAsync(clientId, "project", null, ReviewTargetLifecycle.Disabled, 1, 25)).TotalCount);
            Assert.True(await repository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 2, ReviewTargetLifecycle.Removed));
            var remainingFirst = await repository.GetManagementTargetPageAsync(clientId, null, null, null, 1, 25);
            var remainingLast = await repository.GetManagementTargetPageAsync(clientId, null, null, null, 2, 25);
            Assert.Equal(29, remainingFirst.TotalCount);
            Assert.Equal(29, remainingFirst.Items.Count + remainingLast.Items.Count);
            Assert.DoesNotContain(remainingFirst.Items.Concat(remainingLast.Items), item => item.Id == target.Id);
            Assert.Equal(30, (await repository.GetManagementTargetsAsync(clientId)).Count);
            var removed = (await repository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(ReviewTargetLifecycle.Removed, removed.ReviewTargetLifecycle);
            Assert.Equal(3, removed.ReviewTargetRevision);
            Assert.Null(await repository.GetReviewTargetPolicySnapshotAsync(target.Id, Guid.NewGuid()));
        }
        finally
        {
            await context.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Lifecycle_WaitsForAdmissionAndRestoresIdentityWithoutCrawling()
    {
        fixture.SkipIfUnavailable();
        var options = this.LockTestOptions(out var applicationName);
        await using var seed = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        seed.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Lifecycle test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
        await seed.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(seed);
            var target = await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "native", "repo");
            Assert.Equal(ReviewTargetLifecycle.Enabled, target.ReviewTargetLifecycle);
            await repository.SetActiveAsync(target.Id, clientId, true);
            await using var writer = new MeisterProPRDbContext(options);
            var otherRepository = new CrawlConfigurationRepository(writer);
            var active = (await otherRepository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            await using var admission = await repository.AcquireReviewTargetAdmissionAsync(clientId);
            var disabling = otherRepository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, active.ReviewTargetRevision, ReviewTargetLifecycle.Disabled);
            await this.WaitForPendingAdmissionAsync(applicationName);
            Assert.False(disabling.IsCompleted);
            await admission.DisposeAsync();
            Assert.True(await disabling.WaitAsync(TimeSpan.FromSeconds(10)));
            var disabled = (await otherRepository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.False(disabled.IsActive);
            Assert.Empty(await otherRepository.GetAllActiveAsync());
            Assert.False(
                await otherRepository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, target.ReviewTargetRevision, ReviewTargetLifecycle.Enabled));
            Assert.True(
                await otherRepository.ChangeReviewTargetLifecycleAsync(target.Id, clientId, disabled.ReviewTargetRevision, ReviewTargetLifecycle.Removed));
            Assert.False(await otherRepository.UpdateReviewTargetPolicyAsync(target, clientId, [], ["main"]));
            var removed = (await otherRepository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.True(await otherRepository.RestoreReviewTargetAsync(removed, clientId, ["main"]));
            var restored = (await otherRepository.GetReviewTargetPolicySnapshotAsync(target.Id, clientId))!;
            Assert.Equal(target.Id, restored.Id);
            Assert.False(restored.IsActive);
            Assert.Equal(ReviewTargetLifecycle.Enabled, restored.ReviewTargetLifecycle);
            Assert.Equal(["main"], restored.RepoFilters[0].TargetBranchPatterns);
            Assert.Equal(1, await seed.CrawlConfigurations.CountAsync(config => config.ClientId == clientId));
        }
        finally
        {
            await seed.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task CreateTarget_ConcurrentRetriesAndDistinctRepositoriesPreserveLegacyIdentity()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var seed = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        seed.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Repository target test", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
        seed.CrawlConfigurations.Add(
            new CrawlConfigurationRecord
            {
                Id = legacyId, ClientId = clientId, Provider = ScmProvider.GitHub,
                OrganizationUrl = "https://github.example.test", ProjectId = "owner", IsActive = false,
                CreatedAt = DateTimeOffset.UtcNow,
                RepoFilters =
                [
                    new CrawlRepoFilterRecord
                        { Id = Guid.NewGuid(), RepositoryName = "legacy", SourceProvider = "GitHub", CanonicalSourceRef = "legacy", TargetBranchPatterns = [] }
                ],
            });
        await seed.SaveChangesAsync();
        try
        {
            var barrier = new ConcurrentInsertBarrier();
            var statuses = new System.Collections.Concurrent.ConcurrentBag<int?>();

            async Task<ClientReviewTargetResponse> CreateAsync(string repositoryId)
            {
                var requestOptions = repositoryId == "repo-a"
                    ? new DbContextOptionsBuilder<MeisterProPRDbContext>(options).AddInterceptors(barrier).Options
                    : options;
                await using var context = new MeisterProPRDbContext(requestOptions);
                var connections = Substitute.For<IClientScmConnectionRepository>();
                connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
                    new ClientScmConnectionDto(
                        connectionId, clientId, ScmProvider.GitHub, "https://github.example.test", ScmAuthenticationKind.PersonalAccessToken,
                        "Test", true, "verified", DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
                discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "owner", Arg.Any<CancellationToken>())
                    .Returns(call =>
                        Task.FromResult<IReadOnlyList<RepositoryRef>>(
                            [new RepositoryRef(call.ArgAt<ProviderHostRef>(1), repositoryId, "owner", $"owner/{repositoryId}")]));
                var providers = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
                providers.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
                providers.GetRepositoryDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
                var http = new DefaultHttpContext();
                http.Items["IsAdmin"] = true;
                var controller = new ClientReviewTargetsController(new CrawlConfigurationRepository(context), connections, providerRegistry: providers)
                {
                    ControllerContext = new ControllerContext { HttpContext = http },
                };
                var result = Assert.IsAssignableFrom<ObjectResult>(
                    await controller.CreateTarget(clientId, new(connectionId, "owner", repositoryId, repositoryId)));
                Assert.Contains(result.StatusCode, new int?[] { StatusCodes.Status200OK, StatusCodes.Status201Created });
                if (repositoryId == "repo-a")
                {
                    statuses.Add(result.StatusCode);
                }

                return Assert.IsType<ClientReviewTargetResponse>(result.Value);
            }

            Assert.Equal(legacyId, (await CreateAsync("legacy")).Id);
            var targets = await Task.WhenAll(CreateAsync("repo-a"), CreateAsync("repo-a"), CreateAsync("repo-b"));
            Assert.Equal(targets[0].Id, targets[1].Id);
            Assert.Equal(1, barrier.UniqueFailures);
            Assert.Contains(StatusCodes.Status200OK, statuses);
            Assert.Contains(StatusCodes.Status201Created, statuses);
            Assert.NotEqual(targets[0].Id, targets[2].Id);
            Assert.All(targets, target => Assert.False(target.IsActive));
            Assert.Equal(3, await seed.CrawlConfigurations.CountAsync(config => config.ClientId == clientId));
            Assert.Null((await seed.CrawlConfigurations.AsNoTracking().SingleAsync(config => config.Id == legacyId)).RepositoryId);
        }
        finally
        {
            await seed.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("generic")]
    [InlineData("collision")]
    [InlineData("normalized-legacy-collision")]
    public async Task PatchTarget_UnsupportedReplacementPreservesParentFilterAndActivation(string replacement)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var context = new MeisterProPRDbContext(options);
        var clientId = Guid.NewGuid();
        context.Clients.Add(
            new ClientRecord
            {
                Id = clientId, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Target patch test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
        await context.SaveChangesAsync();
        try
        {
            var repository = new CrawlConfigurationRepository(context);
            var target = await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-a", "repo-a");
            if (replacement == "normalized-legacy-collision")
            {
                var legacy = await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://github.example.test/", "OWNER", "repo-b", "repo-b");
                (await context.CrawlConfigurations.FindAsync(legacy.Id))!.RepositoryId = null;
                await context.SaveChangesAsync();
            }
            else
            {
                await repository.AddReviewTargetAsync(clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-b", "repo-b");
            }

            var canonical = new CanonicalSourceReferenceDto("GitHub", "repo-b");
            IReadOnlyList<CrawlRepoFilterRequest> filters = replacement switch
            {
                "empty" => [],
                "generic" => [new("repo-b", [])],
                "multiple" => [new("repo-a", [], new("GitHub", "repo-a")), new("repo-b", [], canonical)],
                _ => [new("repo-b", [], canonical)],
            };
            var http = new DefaultHttpContext();
            http.Items["IsAdmin"] = true;
            var controller = new AdminCrawlConfigsController(
                repository, Substitute.For<IUserRepository>(), Substitute.For<IClientAdminService>(),
                MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute(),
                new MeisterDev.ProPR.Application.Features.Crawling.Configuration.ReviewConfigurationSelectionService(
                    MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute()),
                Substitute.For<IProCursorKnowledgeSourceRepository>(), NullLogger<AdminCrawlConfigsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };

            Assert.IsType<ConflictObjectResult>(
                await controller.PatchCrawlConfiguration(
                    target.Id,
                    new PatchAdminCrawlConfigRequest { IsActive = true, RepoFilters = filters }, new PatchAdminCrawlConfigRequestValidator()));

            await using var verification = new MeisterProPRDbContext(options);
            var stored = await verification.CrawlConfigurations.Include(config => config.RepoFilters).SingleAsync(config => config.Id == target.Id);
            Assert.False(stored.IsActive);
            Assert.Equal("repo-a", stored.RepositoryId);
            var filter = Assert.Single(stored.RepoFilters);
            Assert.Equal(target.RepoFilters[0].Id, filter.Id);
            Assert.Equal("repo-a", filter.CanonicalSourceRef);
        }
        finally
        {
            await context.Clients.Where(client => client.Id == clientId).ExecuteDeleteAsync();
        }
    }

    private sealed class ConcurrentInsertBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public int UniqueFailures;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref this._arrived) == 2)
            {
                this._ready.TrySetResult();
            }

            await this._ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
            {
                Interlocked.Increment(ref this.UniqueFailures);
            }

            return Task.CompletedTask;
        }
    }
}

[CollectionDefinition("RepositoryTargetPostgresIntegration", DisableParallelization = true)]
public sealed class RepositoryTargetPostgresIntegrationCollection : ICollectionFixture<PostgresContainerFixture>;
