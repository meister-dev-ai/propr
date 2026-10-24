// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Crawling;

[Collection("PostgresIntegration4")]
public sealed class DiscussionSynchronizationStoreTests(PostgresContainerFixture postgres)
{
    [SkippableFact]
    public async Task ProgressCheckpointRetainsOwnershipAndDoesNotAdvanceFreshness()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        await using var lease = await store.TryAcquireAsync(target, "revision", true, Interval);
        Assert.NotNull(lease);
        var before = (await store.ReadAsync(target))!;
        await lease.CheckpointProgressAsync("{\"7\":{\"SourceFingerprint\":\"digest\",\"Consumers\":{\"memory\":\"ack\"}}}");
        var after = (await store.ReadAsync(target))!;
        Assert.Equal("ack", after.ReadProgress()["7"].Consumers["memory"]);
        Assert.Equal(before.LeaseToken, after.LeaseToken);
        Assert.Equal(before.ObservationSequence, after.ObservationSequence);
        Assert.Equal(before.RequestedGeneration, after.RequestedGeneration);
        Assert.Equal(before.CompletedGeneration, after.CompletedGeneration);
        Assert.Equal(before.LastObservedAt, after.LastObservedAt);
        Assert.Equal(before.LastFullReconciledAt, after.LastFullReconciledAt);
        Assert.Equal(before.LastReviewRevision, after.LastReviewRevision);
        Assert.False(lease.CompletionIsCurrent);
        Assert.False(await store.IsCurrentObservationAsync(target, lease.ObservedAt));
        Assert.Null(await store.TryAcquireAsync(target, "revision", true, Interval));
        await lease.EnsureCurrentAsync();
        await lease.CompleteAsync(after.ThreadProgressJson, "[]", lease.ObservedAt, true, true, false, "revision", Interval);
    }

    [SkippableTheory]
    [InlineData("expired")]
    [InlineData("lease")]
    [InlineData("fence")]
    [InlineData("collection")]
    [InlineData("retention")]
    public async Task ProgressCheckpointRejectsLostOwnershipAndChangedPolicies(string change)
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        var target = Target();
        await using var lease = await store.TryAcquireAsync(target, null, true, Interval);
        Assert.NotNull(lease);
        await using var changed = await factory.CreateDbContextAsync();
        var state = changed.DiscussionSynchronizationStates.Where(row => row.Id == lease.State.Id);
        switch (change)
        {
            case "expired": clock.Advance(TimeSpan.FromMinutes(3)); break;
            case "lease": await state.ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseToken, Guid.NewGuid())); break;
            case "fence":
                await changed.DiscussionObservationFences.Where(row => row.OwnerStateId == lease.State.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.Token, Guid.NewGuid()));
                break;
            case "collection":
                await state.ExecuteUpdateAsync(set => set.SetProperty(row => row.CollectionGeneration, row => row.CollectionGeneration + 1)); break;
            case "retention": await state.ExecuteUpdateAsync(set => set.SetProperty(row => row.RetentionGeneration, row => row.RetentionGeneration + 1)); break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.CheckpointProgressAsync("{\"changed\":{}}"));
        Assert.Equal("{}", (await store.ReadAsync(target))!.ThreadProgressJson);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressCheckpointSurvivesRestartAndPreservesANewerNotification(bool completeBeforeRestart)
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var factory = this.Factory();
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        var target = Target();
        long generation;
        await using (var lease = await store.TryAcquireAsync(target, "revision", true, Interval))
        {
            Assert.NotNull(lease);
            generation = lease.State.RequestedGeneration;
            await store.RequestAsync(target, "8");
            await lease.CheckpointProgressAsync("{\"7\":{\"Consumers\":{\"memory\":\"ack\",\"miss\":\"ack\"}}}");
            if (completeBeforeRestart)
            {
                await lease.CompleteAsync(
                    "{\"7\":{\"Consumers\":{\"memory\":\"ack\",\"miss\":\"ack\"}}}", "[]", lease.ObservedAt,
                    true, true, false, "revision", Interval);
            }
        }

        clock.Advance(TimeSpan.FromSeconds(16));
        var restarted = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        await using (var retry = await restarted.TryAcquireAsync(target, "revision", false, Interval))
        {
            Assert.NotNull(retry);
            Assert.Equal("ack", retry.State.ReadProgress()["7"].Consumers["memory"]);
            Assert.Equal(generation + 1, retry.State.RequestedGeneration);
            Assert.Equal(completeBeforeRestart, retry.State.CompletedGeneration == generation);
            Assert.True(retry.State.FullRefreshRequested);
        }

        clock.Advance(TimeSpan.FromSeconds(16));
        await using var secondRetry = await restarted.TryAcquireAsync(target, "revision", false, Interval);
        Assert.NotNull(secondRetry);
        Assert.Equal("ack", secondRetry.State.ReadProgress()["7"].Consumers["memory"]);
        Assert.DoesNotContain("miss", secondRetry.State.ReadProgress()["7"].Consumers.Keys);
    }

    [SkippableFact]
    public async Task InterruptedForcedCatchUpRemainsDueWithoutAdvancingFreshness()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        await using (var first = await store.TryAcquireAsync(target, "revision", true, Interval))
        {
            Assert.NotNull(first);
            await first.CompleteAsync("{}", "[]", first.ObservedAt, true, true, false, "revision", Interval);
        }

        var previous = (await store.ReadAsync(target))!;
        await using (var interrupted = await store.TryAcquireAsync(target, "revision", true, Interval))
        {
            Assert.NotNull(interrupted);
        }

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Contains(await store.ListDueAsync(Interval, 128), item => item.Id == previous.Id);
        var current = (await store.ReadAsync(target))!;
        Assert.Equal(previous.LastFullReconciledAt, current.LastFullReconciledAt);
        Assert.Equal(previous.CompletedGeneration, current.CompletedGeneration);
        await using var retry = await store.TryAcquireAsync(target, "revision", false, Interval);
        Assert.NotNull(retry);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedFenceDoesNotTrustUncheckpointedOrOtherConnectionProgress(bool otherConnection)
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        await using (var first = await store.TryAcquireAsync(target, "revision", true, Interval))
        {
            Assert.NotNull(first);
            await first.CompleteAsync(
                "{\"7\":{\"Consumers\":{\"memory\":\"ack\",\"miss\":\"ack\"}}}", "[]", first.ObservedAt,
                true, true, false, "revision", Interval);
        }

        await using (var interrupted = await store.TryAcquireAsync(
                         otherConnection ? target with { ConnectionId = Guid.NewGuid() } : target, "revision", true, Interval))
        {
            Assert.NotNull(interrupted);
            if (otherConnection)
            {
                await interrupted.CheckpointProgressAsync("{\"7\":{\"Consumers\":{\"memory\":\"other-ack\"}}}");
            }
        }

        clock.Advance(TimeSpan.FromSeconds(16));
        await using var retry = await store.TryAcquireAsync(target, "revision", true, Interval);
        Assert.NotNull(retry);
        Assert.Empty(retry.State.ReadProgress()["7"].Consumers);
    }

    [SkippableFact]
    public async Task RetainedReplayUsesItsConfiguredLeaseWithoutChangingNormalDiscussionDefaults()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        IDiscussionSynchronizationStore store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        await using (var replay = await store.TryAcquireRetainedReplayAsync(Target(), TimeSpan.FromMinutes(15)))
        {
            Assert.NotNull(replay);
            Assert.InRange(replay.State.LeaseExpiresAt!.Value - replay.ObservedAt, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
            await replay.EnsureCurrentAsync();
            Assert.InRange(replay.State.LeaseExpiresAt!.Value - clock.GetUtcNow(), TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
        }

        await using var normal = await store.TryAcquireAsync(Target(), null, true, Interval);
        Assert.NotNull(normal);
        Assert.InRange(normal.State.LeaseExpiresAt!.Value - normal.ObservedAt, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3));
    }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(300);

    [SkippableFact]
    public async Task EventDuringRefreshSurvivesAcknowledgmentAndRestart()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var factory = this.Factory();
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        var target = Target();
        await store.RequestAsync(target, "7");
        await using (var lease = await store.TryAcquireAsync(target, "revision", false, Interval))
        {
            Assert.NotNull(lease);
            var generation = lease.State.RequestedGeneration;
            await store.RequestAsync(target, "8");
            await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, true, "revision", Interval);
            var retained = await store.ReadAsync(target);
            Assert.Equal(generation, retained!.CompletedGeneration);
            Assert.Equal(generation + 1, retained.RequestedGeneration);
            Assert.False(retained.IsClosed);
            Assert.False(lease.CompletionIsCurrent);
        }

        var restarted = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        await using var retry = await restarted.TryAcquireAsync(target, "revision", false, Interval);
        Assert.NotNull(retry);
        await retry.CompleteAsync("{}", "[]", retry.ObservedAt, true, true, false, "revision", Interval);
        Assert.True(retry.CompletionIsCurrent);
        var completed = await restarted.ReadAsync(target);
        Assert.Equal(completed!.RequestedGeneration, completed.CompletedGeneration);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigurationChangeDuringFinalCloseRemainsPending(bool retentionChanged)
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        var target = Target();
        await using (var lease = await store.TryAcquireAsync(target, null, true, Interval))
        {
            Assert.NotNull(lease);
            await using var changed = await factory.CreateDbContextAsync();
            var state = changed.DiscussionSynchronizationStates.Where(row => row.Id == lease.State.Id);
            if (retentionChanged)
            {
                await state.ExecuteUpdateAsync(set => set.SetProperty(row => row.RetentionGeneration, row => row.RetentionGeneration + 1));
            }
            else
            {
                await state.ExecuteUpdateAsync(set => set.SetProperty(row => row.CollectionGeneration, row => row.CollectionGeneration + 1));
            }

            await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, true, null, Interval);
            var pending = await store.ReadAsync(target);
            Assert.False(pending!.IsClosed);
            Assert.False(lease.CompletionIsCurrent);
            Assert.True(pending.FullRefreshRequested);
            Assert.True(pending.NextAttemptAt <= clock.GetUtcNow());
        }

        await using var retry = await store.TryAcquireAsync(target, null, false, Interval);
        Assert.NotNull(retry);
        await retry.CompleteAsync("{}", "[]", retry.ObservedAt, true, true, true, null, Interval);
        Assert.True((await store.ReadAsync(target))!.IsClosed);
        Assert.True(retry.CompletionIsCurrent);
    }

    [SkippableFact]
    public async Task TargetLockIsHeldAcrossExpiredLeaseAndUnrelatedTargetsRemainAvailable()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        await using (var lease = await store.TryAcquireAsync(target, "revision", false, Interval))
        {
            Assert.NotNull(lease);
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.Null(await store.TryAcquireAsync(target, "revision", true, Interval));
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.EnsureCurrentAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.CompleteAsync(
                "{}", "[]", lease.ObservedAt,
                true, true, false, "revision", Interval));
            await using var unrelated = await store.TryAcquireAsync(Target(), "revision", false, Interval);
            Assert.NotNull(unrelated);
            await unrelated.CompleteAsync("{}", "[]", unrelated.ObservedAt, true, true, true, "revision", Interval);
        }

        clock.Advance(TimeSpan.FromSeconds(16));
        await using var replacement = await store.TryAcquireAsync(target, "revision", false, Interval);
        Assert.NotNull(replacement);
        Assert.Equal(2, replacement.State.ObservationSequence);
        await replacement.CompleteAsync("{}", "[]", replacement.ObservedAt, true, true, true, "revision", Interval);
    }

    [SkippableFact]
    public async Task FailedConsumerProgressIsDurableAndReconciliationUsesFiveMinutes()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        await using (var lease = await store.TryAcquireAsync(target, "revision", false, Interval))
        {
            Assert.NotNull(lease);
            await lease.CompleteAsync(
                "{\"7\":{\"SourceFingerprint\":\"digest\",\"Consumers\":{\"miss\":\"ack\"}}}", "[]", lease.ObservedAt,
                true, false, false, "revision", Interval);
        }

        Assert.Null(await store.TryAcquireAsync(target, "revision", false, Interval));
        clock.Advance(TimeSpan.FromSeconds(16));
        await using (var retry = await store.TryAcquireAsync(target, "revision", false, Interval))
        {
            Assert.NotNull(retry);
            Assert.Equal("ack", retry.State.ReadProgress()["7"].Consumers["miss"]);
            await retry.CompleteAsync(retry.State.ThreadProgressJson, "[]", retry.ObservedAt, true, true, false, "revision", Interval);
        }

        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Null(await store.TryAcquireAsync(target, "revision", false, Interval));
        clock.Advance(TimeSpan.FromSeconds(1));
        await using var due = await store.TryAcquireAsync(target, "revision", false, Interval);
        Assert.NotNull(due);
        await due.CompleteAsync("{}", "[]", due.ObservedAt, true, true, true, "revision", Interval);
    }

    [SkippableFact]
    public async Task RetryDeferralCannotPostponeANewerEvent()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        await store.RequestAsync(target);
        var failed = await store.ReadAsync(target);
        await store.DeferAsync(failed!.Id, failed.RequestedGeneration, TimeSpan.FromSeconds(15));
        Assert.True((await store.ReadAsync(target))!.NextAttemptAt > clock.GetUtcNow());
        await store.RequestAsync(target, "9");
        await store.DeferAsync(failed.Id, failed.RequestedGeneration, TimeSpan.FromMinutes(5));
        Assert.InRange((clock.GetUtcNow() - (await store.ReadAsync(target))!.NextAttemptAt).Duration(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
        await using var due = await store.TryAcquireAsync(target, null, false, Interval);
        Assert.NotNull(due);
        await due.CompleteAsync("{}", "[]", due.ObservedAt, true, true, true, null, Interval);
    }

    [SkippableFact]
    public async Task DueSelectionIncludesAnotherClientBeforeTheBatchLimit()
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var now = DateTimeOffset.UtcNow.AddDays(1);
        var clock = new FakeTimeProvider(now);
        var firstClient = Guid.NewGuid();
        var secondClient = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var targets = Enumerable.Range(1, 140).Select(id => Target(firstClient) with { PullRequestId = id })
                .Append(Target(secondClient)).ToArray();
            db.DiscussionSynchronizationStates.AddRange(
                targets.Select(target => new DiscussionSynchronizationState
                {
                    Id = DiscussionSynchronizationState.Identity(
                        target.ClientId, target.ConnectionId, target.Provider,
                        target.ProviderScopePath, target.ProviderProjectKey, target.RepositoryId, target.PullRequestId),
                    ClientId = target.ClientId, ConnectionId = target.ConnectionId, Provider = target.Provider,
                    ProviderScopePath = target.ProviderScopePath, ProviderProjectKey = target.ProviderProjectKey,
                    RepositoryId = target.RepositoryId, PullRequestId = target.PullRequestId, NextAttemptAt = now.AddMinutes(-1),
                }));
            await db.SaveChangesAsync();
        }

        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        var due = await store.ListDueAsync(Interval, 128);
        Assert.Equal(128, due.Count);
        Assert.Contains(due, state => state.ClientId == secondClient);
        await using var cleanup = await factory.CreateDbContextAsync();
        await cleanup.DiscussionSynchronizationStates.Where(state => state.ClientId == firstClient || state.ClientId == secondClient).ExecuteDeleteAsync();
    }

    [SkippableFact]
    public async Task PooledContextCanReadAndAcquireAfterReleasingTheObservationLock()
    {
        postgres.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(postgres.ConnectionString, o => o.UseVector())
            .AddInterceptors(new DiscussionWriteFenceInterceptor()).Options;
        var store = new DiscussionSynchronizationStore(new PooledDbContextFactory<MeisterProPRDbContext>(options), LocalScmPolicies.Registry);
        var target = Target();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await store.RequestAsync(target);
            await using (var lease = await store.TryAcquireAsync(target, null, false, Interval))
            {
                Assert.NotNull(lease);
                await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, false, null, Interval);
            }

            var saved = await store.ReadAsync(target);
            Assert.NotNull(saved!.LastObservedAt);
            Assert.Equal(saved.RequestedGeneration, saved.CompletedGeneration);
        }
    }

    [SkippableFact]
    public async Task DownstreamLifecycleFailureMakesACompletedClosedObservationDueAgain()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        long generation;
        await using (var lease = await store.TryAcquireAsync(target, null, true, Interval))
        {
            Assert.NotNull(lease);
            generation = lease.State.RequestedGeneration;
            await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, true, null, Interval);
        }

        Assert.True((await store.ReadAsync(target))!.IsClosed);
        await store.DeferAsync((await store.ReadAsync(target))!.Id, generation, TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Contains(await store.ListDueAsync(Interval, 128), row => row.ClientId == target.ClientId && row.PullRequestId == target.PullRequestId);
    }

    [SkippableFact]
    public async Task HistoricalObservationCannotOverwriteANewerUnchangedCurrentPass()
    {
        postgres.SkipIfUnavailable();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry, clock);
        var target = Target();
        var scope = ProviderSourceIdentity.FromReviewSource(
            target.Provider, target.ProviderScopePath,
            LocalScmPolicies.Registry.GetSourceIdentityPolicy(target.Provider)).Value;
        var key = new CodeInsightPullRequestKey(target.ClientId, target.RepositoryId, target.PullRequestId);
        var connection = new ClientScmConnectionDto(
            target.ConnectionId, target.ClientId, target.Provider, target.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, clock.GetUtcNow(), clock.GetUtcNow());
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByClientIdAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns([connection]);
        connections.GetByIdAsync(target.ClientId, target.ConnectionId, Arg.Any<CancellationToken>()).Returns(connection);
        connections.GetOperationalConnectionByIdAsync(target.ClientId, target.ConnectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionCredentialDto(
                target.ConnectionId, target.ClientId, target.Provider, target.ProviderScopePath,
                ScmAuthenticationKind.PersonalAccessToken, "Test", "synthetic", true));
        var finding = new CodeInsightFindingView(
            Guid.NewGuid(), Guid.NewGuid(), "7", 0, "file.cs", 1, CommentSeverity.Warning,
            "Concern", "1", clock.GetUtcNow(), ProviderScope: scope, ProviderCommentId: "1");
        var findings = Substitute.For<ICodeInsightFindingStore>();
        findings.GetNativeFeedbackFindingsAsync(key, scope, Arg.Any<CancellationToken>()).Returns(
            [new CodeInsightNativeFindingSnapshot(finding, CodeInsightPublicationState.Published, null, null)]);
        var batches = Substitute.For<ICodeInsightDiscussionBatchFactory>();
        batches.CreateAsync(key, scope, Arg.Any<CancellationToken>()).Returns(new CodeInsightDiscussionBatch(key, scope, [finding], "v1"));
        var origins = Substitute.For<IPostedCommentOriginStore>();
        origins.GetOriginsForProviderSourceAsync(
            target.ClientId, target.RepositoryId, target.PullRequestId, target.Provider, scope,
            Arg.Any<CancellationToken>()).Returns(new PostedCommentOriginSourceSnapshot([], []));
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var misses = Substitute.For<ICodeInsightMissHarvester>();
        misses.ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(new CodeInsightEvidenceAcknowledgment(true));
        var dispositions = Substitute.For<ICodeInsightDispositionService>();
        var outcomes = new List<string?>();
        dispositions.ObserveCurrentAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            outcomes.Add(call.Arg<ThreadResolvedDomainEvent>().NativeStatus);
            return new CodeInsightEvidenceAcknowledgment(true);
        });
        dispositions.ObserveHistoricalAsync(Arg.Any<CodeInsightNativeFindingSnapshot>(), Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                outcomes.Add(call.Arg<ThreadResolvedDomainEvent>().NativeStatus);
                return new CodeInsightEvidenceAcknowledgment(true);
            });
        var coverage = Substitute.For<ICodeInsightHarvestCoverageRecorder>();
        coverage.RecordAcknowledgedAsync(key, scope, Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new CodeInsightEvidenceAcknowledgment(true));
        coverage.RecordCurrentAcknowledgedAsync(key, scope, Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new CodeInsightEvidenceAcknowledgment(true));
        var reader = Substitute.For<IPullRequestThreadSnapshotReader>();
        reader.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(
                clock.GetUtcNow(), [new("1", "file.cs", 1, [new("Bot", "Concern", CommentId: 1)], "Active")],
                [new("1", "Active", "file.cs", "Concern")]));
        var currentService = new DiscussionSynchronizationService(
            store, reader, connections, LocalScmPolicies.Registry,
            NullLogger<DiscussionSynchronizationService>.Instance, Microsoft.Extensions.Options.Options.Create(new DiscussionSynchronizationOptions()),
            origins: origins, batches: batches, dispositions: dispositions, misses: misses, coverage: coverage);

        Task<PullRequestThreadSnapshot> CurrentAsync() => currentService.ObserveAsync(
            new()
            {
                ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "test", ClientId = target.ClientId, ConnectionId = target.ConnectionId,
                Provider = target.Provider, ProviderScopePath = target.ProviderScopePath, ProviderProjectKey = target.ProviderProjectKey,
                RepositoryId = target.RepositoryId, PullRequestId = target.PullRequestId, PullRequestStatus = PrStatus.Active, CandidateIterationId = 7,
            });

        await CurrentAsync();
        var digest = (await store.ReadAsync(target))!.ReadProgress()["1"].SourceFingerprint;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CodeInsightProviderFeedback>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedback = Substitute.For<ICodeInsightProviderFeedbackReader>();
        feedback.ReadAsync(
            Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
            CodeInsightObservationMode.HistoricalBackfill).Returns(_ =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var historical = new PullRequestCloseObserver(
            LocalScmPolicies.Registry, NullLogger<PullRequestCloseObserver>.Instance,
            missHarvester: misses, findingStore: findings, collectionGate: gate, scmConnectionRepository: connections, postedCommentOriginStore: origins,
            dispositionService: dispositions, harvestCoverageRecorder: coverage, feedbackReader: feedback, discussionStore: store, snapshotReader: reader,
            discussionSynchronization: currentService);
        clock.Advance(TimeSpan.FromSeconds(301));
        var pending = historical.RefreshAsync(new(key, target.Provider, target.ProviderScopePath, target.ProviderProjectKey, scope, target.ConnectionId));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.RequestAsync(target);
        var concurrent = await CurrentAsync();
        Assert.False(concurrent.EnumerationComplete);
        Assert.Equal(["Active"], outcomes);
        release.SetResult(new([new("1", "Fixed", "file.cs", "Concern", CodeChangedSinceRaised: ThreadAnchorCodeChange.Changed)], []));
        Assert.Equal(CodeInsightObservationStatus.Observed, (await pending).Status);
        clock.Advance(TimeSpan.FromSeconds(16));
        await CurrentAsync();
        Assert.Equal(["Active", "Fixed", "Active"], outcomes);
        Assert.Equal(digest, (await store.ReadAsync(target))!.ReadProgress()["1"].SourceFingerprint);
    }

    [SkippableFact]
    public async Task SupersededObservationRejectsEvidenceButPreservesAlreadySpentTokenUsage()
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var target = Target();
        var retainedId = await SeedRetainedAsync(factory, target);
        await using var db = await factory.CreateDbContextAsync();
        db.Clients.Add(
            new ClientRecord
            {
                Id = target.ClientId, TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Test", IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        using (DiscussionWriteFenceContext.Begin(new(Guid.NewGuid(), Guid.NewGuid())))
        {
            await new ClientTokenUsageRepository(db).UpsertAsync(
                target.ClientId, "model", DateOnly.FromDateTime(DateTime.UtcNow),
                37, 11, CancellationToken.None, estimatedCostUsd: 0.25m);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.RetainedPullRequests.Where(row => row.Id == retainedId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.PrState, "stale")));
        }

        var usage = await db.ClientTokenUsageSamples.SingleAsync(row => row.ClientId == target.ClientId);
        Assert.Equal(37, usage.InputTokens);
        Assert.Equal(11, usage.OutputTokens);
        Assert.Equal(0.25m, usage.EstimatedCostUsd);
        Assert.Equal("unchanged", (await db.RetainedPullRequests.SingleAsync(row => row.Id == retainedId)).PrState);
    }

    [SkippableFact]
    public async Task ConsumerWritesPropagateAcrossAwaitAndFenceExecuteUpdateWithoutAnExplicitTransaction()
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry);
        var target = Target();
        var retainedId = await SeedRetainedAsync(factory, target);
        await using var lease = await store.TryAcquireAsync(target, null, true, Interval);
        Assert.NotNull(lease);
        using (lease.BeginWrites())
        {
            await Task.Yield();
            await using var sink = await factory.CreateDbContextAsync();
            Assert.Equal(
                1, await sink.RetainedPullRequests.Where(row => row.Id == retainedId)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.PrState, "observed")));
            Assert.Null(sink.Database.CurrentTransaction);
        }

        await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, false, null, Interval);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal("observed", (await verify.RetainedPullRequests.SingleAsync(row => row.Id == retainedId)).PrState);
    }

    [SkippableFact]
    public async Task CanceledConsumerWriteRollsBackAndAllowsTheNextWrite()
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry);
        var target = Target();
        var retainedId = await SeedRetainedAsync(factory, target);
        await using var lease = await store.TryAcquireAsync(target, null, true, Interval);
        Assert.NotNull(lease);
        using (lease.BeginWrites())
        {
            await using var sink = await factory.CreateDbContextAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE retained_pull_requests SET pr_state='canceled' WHERE id={retainedId} AND pg_sleep(10) IS NULL", timeout.Token));
            Assert.Equal("unchanged", (await sink.RetainedPullRequests.SingleAsync(row => row.Id == retainedId)).PrState);
            Assert.Equal(
                1, await sink.RetainedPullRequests.Where(row => row.Id == retainedId)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.PrState, "retry")));
        }

        await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, false, null, Interval);
    }

    [SkippableFact]
    public async Task DisconnectedObserverCannotWriteAfterANewerUnchangedObservation()
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry);
        var target = Target();
        var retainedId = await SeedRetainedAsync(factory, target);
        var old = await store.TryAcquireAsync(target, null, true, Interval);
        Assert.NotNull(old);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task StaleConsumerAsync()
        {
            await resume.Task;
            await using var sink = await factory.CreateDbContextAsync();
            await sink.RetainedPullRequests.Where(row => row.Id == retainedId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.PrState, "stale"));
        }

        Task stale;
        using (old.BeginWrites())
        {
            stale = StaleConsumerAsync();
        }

        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
        {
            await admin.OpenAsync();
            var scope = ProviderSourceIdentity.FromReviewSource(
                target.Provider, target.ProviderScopePath,
                LocalScmPolicies.Registry.GetSourceIdentityPolicy(target.Provider)).Value;
            var sourceId = DiscussionSynchronizationState.Identity(
                target.ClientId, Guid.Empty, target.Provider, scope, "", target.RepositoryId, target.PullRequestId);
            await using var terminate = new NpgsqlCommand(
                """
                SELECT pg_terminate_backend(pid) FROM pg_locks
                WHERE locktype='advisory' AND granted AND objsubid=1
                  AND classid=((hashtextextended(@key,0)>>32)&4294967295)::oid
                  AND objid=(hashtextextended(@key,0)&4294967295)::oid
                """, admin);
            terminate.Parameters.AddWithValue("key", $"discussion-source:{sourceId:D}");
            Assert.Equal(true, await terminate.ExecuteScalarAsync());
        }

        await using (var newer = await store.TryAcquireAsync(target, null, true, Interval))
        {
            Assert.NotNull(newer);
            await newer.CompleteAsync("{}", "[]", newer.ObservedAt, true, true, false, null, Interval);
        }

        resume.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale);
        await old.DisposeAsync();
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal("unchanged", (await verify.RetainedPullRequests.SingleAsync(row => row.Id == retainedId)).PrState);
    }

    [SkippableFact]
    public async Task CapturedConnectionsForTheSameSourceShareObservationOrdering()
    {
        postgres.SkipIfUnavailable();
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry);
        var target = Target();
        var other = target with { ConnectionId = Guid.NewGuid() };
        await using (var first = await store.TryAcquireAsync(target, null, true, Interval))
        {
            Assert.NotNull(first);
            Assert.Null(await store.TryAcquireAsync(other, null, true, Interval));
            await first.CompleteAsync("{}", "[]", first.ObservedAt, true, true, false, null, Interval);
        }

        await using var second = await store.TryAcquireAsync(other, null, true, Interval);
        Assert.NotNull(second);
        await second.CompleteAsync("{}", "[]", second.ObservedAt, true, true, false, null, Interval);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedObservationCannotAcknowledgeEvidenceAfterAnotherCapturedConnectionObserves(bool newerCompleted)
    {
        postgres.SkipIfUnavailable();
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry);
        var target = Target();
        var connection = new ClientScmConnectionDto(
            target.ConnectionId, target.ClientId, target.Provider, target.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(target.ClientId, target.ConnectionId, Arg.Any<CancellationToken>()).Returns(connection);
        connections.GetOperationalConnectionByIdAsync(target.ClientId, target.ConnectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionCredentialDto(
                target.ConnectionId, target.ClientId, target.Provider, target.ProviderScopePath,
                ScmAuthenticationKind.PersonalAccessToken, "Test", "synthetic", true));
        var reader = Substitute.For<IPullRequestThreadSnapshotReader>();
        reader.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], []));
        var batches = Substitute.For<ICodeInsightDiscussionBatchFactory>();
        batches.CreateAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new CodeInsightDiscussionBatch(call.Arg<CodeInsightPullRequestKey>(), call.Arg<string>(), [], "v1"));
        var coverage = Substitute.For<ICodeInsightHarvestCoverageRecorder>();
        coverage.RecordCurrentAcknowledgedAsync(
                Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new CodeInsightEvidenceAcknowledgment(true));
        var service = new DiscussionSynchronizationService(
            store, reader, connections, LocalScmPolicies.Registry,
            NullLogger<DiscussionSynchronizationService>.Instance, Microsoft.Extensions.Options.Options.Create(new DiscussionSynchronizationOptions()),
            batches: batches, coverage: coverage, misses: Substitute.For<ICodeInsightMissHarvester>(),
            dispositions: Substitute.For<ICodeInsightDispositionService>());
        var request = new PullRequestSynchronizationRequest
        {
            ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "test", ClientId = target.ClientId, ConnectionId = target.ConnectionId,
            Provider = target.Provider, ProviderScopePath = target.ProviderScopePath, ProviderProjectKey = target.ProviderProjectKey,
            RepositoryId = target.RepositoryId, PullRequestId = target.PullRequestId, PullRequestStatus = PrStatus.Active, CandidateIterationId = 7,
        };
        var first = await service.ObserveAsync(request);
        Assert.True(first.CurrentConsumersRetained);
        await using (var newer = await store.TryAcquireAsync(target with { ConnectionId = Guid.NewGuid() }, "7", true, Interval))
        {
            Assert.NotNull(newer);
            if (newerCompleted)
            {
                await newer.CompleteAsync("{}", "[]", newer.ObservedAt, true, true, false, "7", Interval);
            }

            var subsequent = await service.ObserveAsync(request);
            Assert.NotSame(first, subsequent);
            Assert.False(subsequent.CurrentConsumersRetained);
        }
    }

    [SkippableFact]
    public async Task ConcurrentLeaseRenewalsUseOneDatabaseOperationAtATime()
    {
        postgres.SkipIfUnavailable();
        var store = new DiscussionSynchronizationStore(this.Factory(), LocalScmPolicies.Registry);
        await using var lease = await store.TryAcquireAsync(Target(), null, true, Interval);
        Assert.NotNull(lease);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => lease.EnsureCurrentAsync()));
        await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, false, null, Interval);
    }

    [SkippableFact]
    public async Task FailedFenceRenewalDoesNotExtendTheStateLease()
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry, clock);
        var target = Target();
        await using var lease = await store.TryAcquireAsync(target, null, true, Interval);
        Assert.NotNull(lease);
        var expiry = (await store.ReadAsync(target))!.LeaseExpiresAt;
        await using var other = await factory.CreateDbContextAsync();
        var source = target.GetEvidenceSourceId(LocalScmPolicies.Registry);
        await other.DiscussionObservationFences.Where(row => row.Id == source).ExecuteUpdateAsync(set => set.SetProperty(row => row.Token, Guid.NewGuid()));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.EnsureCurrentAsync());
        Assert.Equal(expiry, (await store.ReadAsync(target))!.LeaseExpiresAt);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousEvidenceCommandsCommitWithinTheirValidatedFence(bool trackedSave)
    {
        postgres.SkipIfUnavailable();
        var factory = this.Factory();
        var target = Target();
        var retainedId = await SeedRetainedAsync(factory, target);
        var store = new DiscussionSynchronizationStore(factory, LocalScmPolicies.Registry);
        await using var lease = await store.TryAcquireAsync(target, null, true, Interval);
        Assert.NotNull(lease);
        using (lease.BeginWrites())
        {
            using var sink = factory.CreateDbContext();
            if (trackedSave)
            {
                sink.RetainedPullRequests.Single(row => row.Id == retainedId).PrState = "observed";
                sink.SaveChanges();
            }
            else
            {
                Assert.Equal(
                    1, sink.RetainedPullRequests.Where(row => row.Id == retainedId).ExecuteUpdate(set => set.SetProperty(row => row.PrState, "observed")));
            }

            Assert.Null(sink.Database.CurrentTransaction);
        }

        await lease.CompleteAsync("{}", "[]", lease.ObservedAt, true, true, false, null, Interval);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal("observed", (await verify.RetainedPullRequests.SingleAsync(row => row.Id == retainedId)).PrState);
    }

    private static async Task<Guid> SeedRetainedAsync(FactoryImpl factory, PullRequestThreadSnapshotTarget target)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = new RetainedPullRequest
        {
            Id = Guid.NewGuid(), ClientId = target.ClientId, ConnectionId = target.ConnectionId,
            RepositoryId = target.RepositoryId, PullRequestId = target.PullRequestId,
            PrState = "unchanged", LastActivityAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.RetainedPullRequests.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private FactoryImpl Factory() => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(postgres.ConnectionString, options => options.UseVector()).AddInterceptors(new DiscussionWriteFenceInterceptor()).Options);

    private static PullRequestThreadSnapshotTarget Target(Guid? clientId = null) =>
        new(clientId ?? Guid.NewGuid(), Guid.NewGuid(), ScmProvider.AzureDevOps, "https://dev.azure.com/org", "project", "repo", 42);

    private sealed class FactoryImpl(DbContextOptions<MeisterProPRDbContext> options) : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext() => new(options);
        public Task<MeisterProPRDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(this.CreateDbContext());
    }
}
