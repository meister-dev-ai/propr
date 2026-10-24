// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class DiscussionSynchronizationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveAdmissionDoesNotWaitForMemoryAndLeavesItDue(bool prepare)
    {
        var harness = CreateResolvedMemoryHarness();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(release.Task);
        var jobs = Substitute.For<IJobRepository>();
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>()).Returns(new TryAddReviewJobResult(true, null, 0));
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance, discussions: harness.CreateService());
        try
        {
            var admission = prepare
                ? CompletePreparedAsync()
                : service.SynchronizeAsync(harness.Request);
            var outcome = await admission.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
            await harness.Memory.DidNotReceive().ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
            Assert.True(harness.State.FullRefreshRequested);
            Assert.True(harness.State.CompletedGeneration < harness.State.RequestedGeneration);
        }
        finally
        {
            release.TrySetResult(true);
        }

        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        var registry = CreateReviewRegistry(CodeReviewState.Open);
        await harness.CreateService(registry: registry).ObserveAsync(harness.Request with { DiscussionOnly = true });
        await harness.Memory.Received(1).ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
        Assert.Equal(harness.State.RequestedGeneration, harness.State.CompletedGeneration);

        async Task<PullRequestSynchronizationOutcome> CompletePreparedAsync()
        {
            var prepared = await service.PrepareAsync(harness.Request);
            return await prepared.CompleteAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionThatObservesClosureStillProcessesMemory(bool prepare)
    {
        var harness = CreateResolvedMemoryHarness();
        var registry = CreateReviewRegistry(CodeReviewState.Closed);
        var jobs = Substitute.For<IJobRepository>();
        var service = new PullRequestSynchronizationService(
            registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance, discussions: harness.CreateService(registry: registry));
        var request = harness.Request with { ActivationSource = PullRequestActivationSource.Webhook };
        if (prepare)
        {
            var prepared = await service.PrepareAsync(request);
            await prepared.CompleteAsync();
        }
        else
        {
            await service.SynchronizeAsync(request);
        }

        await harness.Memory.Received(1).ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        Assert.True(harness.State.IsClosed);
    }

    [Fact]
    public async Task CompletedMemorySurvivesCancellationWhileAnotherThreadIsProcessing()
    {
        var harness = CreateResolvedMemoryHarness();
        harness.Threads = [harness.Threads[0], harness.Threads[0] with { ThreadId = "2" }];
        harness.ReviewerThreads = [harness.ReviewerThreads[0], harness.ReviewerThreads[0] with { ThreadId = "2" }];
        using var cancellation = new CancellationTokenSource();
        harness.Memory.ObserveResolvedAsync(Arg.Is<ThreadResolvedDomainEvent>(evt => evt.ThreadId == "2"), Arg.Any<CancellationToken>()).Returns(call =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<bool>(call.Arg<CancellationToken>());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.CreateService().ObserveAsync(harness.Request, cancellation.Token));
        Assert.True(harness.State.ReadProgress()["1"].Consumers.ContainsKey("memory"));
        Assert.False(harness.State.ReadProgress()["2"].Consumers.ContainsKey("memory"));
        harness.Memory.ClearReceivedCalls();
        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        await harness.CreateService().ObserveAsync(harness.Request);
        await harness.Memory.DidNotReceive().ObserveResolvedAsync(Arg.Is<ThreadResolvedDomainEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        await harness.Memory.Received(1).ObserveResolvedAsync(Arg.Is<ThreadResolvedDomainEvent>(evt => evt.ThreadId == "2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnrelatedRevisionDoesNotRegenerateAcknowledgedMemory()
    {
        var harness = CreateResolvedMemoryHarness();
        await harness.ObserveAsync();
        harness.Memory.ClearReceivedCalls();
        harness.Scans.ClearReceivedCalls();
        harness.Checkpoints = 0;
        await harness.CreateService().ObserveAsync(harness.Request with { CandidateIterationId = 8 });
        await harness.Memory.DidNotReceive().ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
        await harness.Scans.DidNotReceive().SetLastSeenStatusesAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, harness.Checkpoints);
    }

    [Fact]
    public async Task InterruptedReplacementCannotReuseThePreviousMemoryAcknowledgmentWhenContentReverts()
    {
        var harness = CreateResolvedMemoryHarness();
        await harness.ObserveAsync();
        var originalThread = harness.Threads[0];
        var originalStatus = harness.ReviewerThreads[0];
        harness.Threads[0] = originalThread with { Comments = [new("Human", "Changed resolution", CommentId: 1)] };
        harness.ReviewerThreads[0] = originalStatus with { CommentHistory = "Changed resolution" };
        using var cancellation = new CancellationTokenSource();
        var oldAcknowledgmentWasRemoved = false;
        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            oldAcknowledgmentWasRemoved = !harness.State.ReadProgress()["1"].Consumers.ContainsKey("memory");
            cancellation.Cancel();
            return Task.FromCanceled<bool>(call.Arg<CancellationToken>());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.CreateService().ObserveAsync(harness.Request, cancellation.Token));
        Assert.True(oldAcknowledgmentWasRemoved);
        harness.Threads[0] = originalThread;
        harness.ReviewerThreads[0] = originalStatus;
        harness.Memory.ClearReceivedCalls();
        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("history")]
    public async Task RelevantMemoryChangeReprocessesAcknowledgedResolution(string change)
    {
        var harness = CreateResolvedMemoryHarness();
        await harness.ObserveAsync();
        harness.Memory.ClearReceivedCalls();
        if (change == "configuration")
        {
            harness.MemoryVersion = "updated-routing";
        }
        else
        {
            harness.Threads[0] = harness.Threads[0] with { Comments = [new("Human", "Updated resolution", CommentId: 1)] };
            harness.ReviewerThreads[0] = harness.ReviewerThreads[0] with { CommentHistory = "Updated resolution" };
        }

        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
    }

    private static Harness CreateResolvedMemoryHarness()
    {
        var harness = new Harness
        {
            EnableMemory = true,
            Threads = [new("1", "a.cs", 1, [new("Reviewer", "Resolved concern", CommentId: 1)], "Fixed")],
            ReviewerThreads = [new("1", "Fixed", "a.cs", "Resolved concern", CodeChangedSinceRaised: ThreadAnchorCodeChange.Changed)]
        };
        harness.Scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = harness.Scan.Id, ThreadId = "1", LastSeenStatus = "Fixed" });
        return harness;
    }

    private static IScmProviderRegistry CreateReviewRegistry(CodeReviewState state)
    {
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => LocalScmPolicies.Registry.GetReviewSourcePolicy(call.Arg<ScmProvider>()));
        var query = Substitute.For<ICodeReviewQueryService>();
        registry.GetCodeReviewQueryService(ScmProvider.AzureDevOps).Returns(query);
        query.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>())
            .Returns(call =>
            {
                var review = call.Arg<CodeReviewRef>();
                return new ReviewDiscoveryItemDto(
                    ScmProvider.AzureDevOps, review.Repository, review, state, new ReviewRevision("head", "base", null, "7", null), null,
                    "Review", null, "source", "main");
            });
        return registry;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThrownModelPreparationOrFinalizationDoesNotBlockLegacyConsumers(bool preparation)
    {
        var harness = new Harness();
        var models = Substitute.For<ICodeInsightModelConcernObserver>();
        models.PrepareAsync(Arg.Any<CodeInsightDiscussionBatch>(), Arg.Any<CancellationToken>()).Returns("models");
        models.ObserveAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(new ModelConcernAcknowledgment(true, true));
        models.CompleteEnumerationAsync(
            Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<bool>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        if (preparation)
        {
            models.PrepareAsync(Arg.Any<CodeInsightDiscussionBatch>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("proof reader unavailable"));
        }
        else
        {
            models.CompleteEnumerationAsync(
                    Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(),
                    Arg.Any<bool>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("proof finalization unavailable"));
        }

        harness.Models = models;
        var snapshot = await harness.ObserveAsync();
        Assert.True(snapshot.CurrentConsumersRetained);
        await harness.Misses.Received(2).ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedModelAttributionKeepsLegacyCoverageAndRetriesOnlyItsOwnConsumer()
    {
        var harness = new Harness();
        var models = Substitute.For<ICodeInsightModelConcernObserver>();
        models.PrepareAsync(Arg.Any<CodeInsightDiscussionBatch>(), Arg.Any<CancellationToken>()).Returns("model-dependencies-v1");
        models.ObserveAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(new ModelConcernAcknowledgment(true, false));
        models.CompleteEnumerationAsync(
            Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<bool>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        harness.Models = models;
        var service = harness.CreateService();
        var observed = await service.ObserveAsync(harness.Request);
        await models.Received(2).ObserveAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
        Assert.True(observed.CurrentConsumersRetained);
        Assert.True(harness.State.CompletedGeneration < harness.State.RequestedGeneration || harness.State.LastFullReconciledAt is null);
        harness.Misses.ClearReceivedCalls();
        models.ObserveAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(new ModelConcernAcknowledgment(true, true));
        await service.ObserveAsync(harness.Request);
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
        await models.Received(4).ObserveAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangedModelDependencyReplaysOnlyModelConsumerEvenWithUnchangedThreads()
    {
        var harness = new Harness();
        var models = Substitute.For<ICodeInsightModelConcernObserver>();
        var version = "one";
        models.PrepareAsync(Arg.Any<CodeInsightDiscussionBatch>(), Arg.Any<CancellationToken>()).Returns(_ => version);
        models.ObserveAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(new ModelConcernAcknowledgment(true, true));
        models.CompleteEnumerationAsync(
            Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<bool>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        harness.Models = models;
        var service = harness.CreateService();
        await service.ObserveAsync(harness.Request);
        harness.Misses.ClearReceivedCalls();
        version = "two";
        await service.ObserveAsync(harness.Request);
        await models.Received(4).ObserveAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnchangedContentSkipsThreadConsumersAndStillRecordsPrFreshness()
    {
        var harness = new Harness();
        await harness.ObserveAsync();
        harness.Archive.ClearReceivedCalls();
        harness.Misses.ClearReceivedCalls();
        await harness.ObserveAsync();
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
        await harness.Archive.Received(1).HandleThreadsUpdatedAsync(
            Arg.Any<PullRequestRetentionKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            Arg.Is<IReadOnlyList<RetainedThreadSnapshot>>(threads => threads.Count == 0), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("Sensitive discussion", harness.State.ThreadProgressJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive discussion", harness.State.ReviewerSummariesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedCommentProcessesOnlyItsThread()
    {
        var harness = new Harness();
        await harness.ObserveAsync();
        harness.Misses.ClearReceivedCalls();
        harness.Threads[0] = harness.Threads[0] with { Comments = [harness.Threads[0].Comments[0] with { Content = "Edited concern" }] };
        await harness.ObserveAsync();
        await harness.Misses.Received(1).ObserveCurrentAsync(Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedArchiveDoesNotRepeatSuccessfulMissCollection()
    {
        var harness = new Harness();
        harness.Archive.HandleThreadsUpdatedAsync(
            Arg.Any<PullRequestRetentionKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyList<RetainedThreadSnapshot>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("unavailable"));
        await harness.ObserveAsync();
        harness.Misses.ClearReceivedCalls();
        harness.Archive.HandleThreadsUpdatedAsync(
            Arg.Any<PullRequestRetentionKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyList<RetainedThreadSnapshot>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await harness.ObserveAsync();
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DependencyChangeCatchesUpWithoutReplacingArchivedComments()
    {
        var harness = new Harness();
        await harness.ObserveAsync();
        harness.Version = "changed-model-or-finding-receipt";
        harness.Misses.ClearReceivedCalls();
        harness.Archive.ClearReceivedCalls();
        await harness.ObserveAsync();
        await harness.Misses.Received(2).ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
        await harness.Archive.Received(1).HandleThreadsUpdatedAsync(
            Arg.Any<PullRequestRetentionKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            Arg.Is<IReadOnlyList<RetainedThreadSnapshot>>(threads => threads.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnablingCollectionCatchesUpWhileRetentionRemainsDisabled()
    {
        var harness = new Harness { Enabled = false, Retention = false };
        await harness.ObserveAsync();
        harness.Enabled = true;
        await harness.ObserveAsync();
        await harness.Misses.Received(2).ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
        await harness.Archive.DidNotReceive().HandleThreadsUpdatedAsync(
            Arg.Any<PullRequestRetentionKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyList<RetainedThreadSnapshot>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACompleteEnumerationTurnsAnAbsentThreadIntoAConsumerDeletion()
    {
        var harness = new Harness();
        await harness.ObserveAsync();
        harness.Threads = [harness.Threads[1]];
        harness.Misses.ClearReceivedCalls();
        await harness.ObserveAsync();
        await harness.Misses.Received(1).ObserveCurrentAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "1" && evt.IsDeleted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncompleteEnumerationDoesNotAcknowledgeAbsentThreads()
    {
        var harness = new Harness();
        await harness.ObserveAsync();
        var before = harness.State.ThreadProgressJson;
        harness.Complete = false;
        harness.Threads = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ObserveAsync());
        Assert.Equal(before, harness.State.ThreadProgressJson);
    }

    [Fact]
    public void FingerprintIgnoresDictionaryAndCommentEnumerationOrderButDetectsSourceChanges()
    {
        var first = new PrCommentThread(
            "1", "a.cs", 2,
            [new("a", "First", CommentId: 1), new("b", "Second", CommentId: 2)], "Active", SourceAnchor: "{\"a\":1,\"b\":2}");
        var reordered = first with { Comments = first.Comments.Reverse().ToArray(), SourceAnchor = "{\"b\":2,\"a\":1}" };
        Assert.Equal(DiscussionFingerprints.CanonicalThread(first), DiscussionFingerprints.CanonicalThread(reordered));
        Assert.NotEqual(DiscussionFingerprints.CanonicalThread(first), DiscussionFingerprints.CanonicalThread(first with { IsDeleted = true }));
        Assert.NotEqual(DiscussionFingerprints.CanonicalThread(first), DiscussionFingerprints.CanonicalThread(first with { LineNumber = 3 }));
        Assert.NotEqual(DiscussionFingerprints.CanonicalThread(first), DiscussionFingerprints.CanonicalThread(first with { Status = "Fixed" }));
    }

    [Fact]
    public async Task FailedStrictDiscussionReadDoesNotPreventFileReviewAdmission()
    {
        var harness = new Harness();
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("incomplete"));
        var jobs = Substitute.For<IJobRepository>();
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>()).Returns(new TryAddReviewJobResult(true, null, 0));
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs,
            NullLogger<PullRequestSynchronizationService>.Instance, discussions: discussions);
        await service.SynchronizeAsync(harness.Request);
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockedDiscussionOnlyRefreshPersistsEvidenceWithoutAdmittingReviewWork()
    {
        var request = new Harness().Request with { DiscussionOnly = true, AllowReviewSubmission = false };
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [], true)
                { ObservedPullRequestStatus = PrStatus.Active, ObservedReviewRevision = new ReviewRevision("head", "base", null, "7", null) });
        var jobs = Substitute.For<IJobRepository>();
        var threadJobs = Substitute.For<IThreadPassJobRepository>();
        var blocked = Substitute.For<IBlockedPullRequestStore>();
        blocked.IsBlockedAsync(
            request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, request.RepositoryId,
            request.PullRequestId, Arg.Any<CancellationToken>()).Returns(true);
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs,
            NullLogger<PullRequestSynchronizationService>.Instance, blockedPullRequestStore: blocked, threadPassJobs: threadJobs,
            discussions: discussions);
        await service.SynchronizeAsync(request);
        await discussions.Received(1).ObserveAsync(request, Arg.Any<CancellationToken>());
        await blocked.Received(1).IsBlockedAsync(
            request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, request.RepositoryId,
            request.PullRequestId, Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        Assert.Empty(threadJobs.ReceivedCalls());
    }

    [Fact]
    public async Task IdentifiedEventPreservesFullFreshnessAndUnaffectedConsumerProgress()
    {
        var harness = new Harness();
        await harness.ObserveAsync();
        var fullFreshness = harness.State.LastFullReconciledAt;
        var untouched = harness.State.ReadProgress()["2"].SourceFingerprint;
        harness.State.RequestedGeneration++;
        harness.State.PendingThreadIdsJson = "[\"1\"]";
        harness.Threads[0] = harness.Threads[0] with { Comments = [harness.Threads[0].Comments[0] with { Content = "Edited" }] };
        harness.Reader.ClearReceivedCalls();
        harness.Misses.ClearReceivedCalls();
        await harness.ObserveAsync();
        await harness.Reader.Received(1).ReadTargetedAsync(
            Arg.Any<PullRequestThreadSnapshotTarget>(),
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "1"), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>());
        await harness.Reader.DidNotReceive().ReadAsync(
            Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>());
        Assert.Equal(fullFreshness, harness.State.LastFullReconciledAt);
        Assert.Equal(untouched, harness.State.ReadProgress()["2"].SourceFingerprint);
        Assert.True(harness.State.LastObservedAt >= fullFreshness);
        await harness.Misses.Received(1).ObserveCurrentAsync(Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncompleteFinalObservationDefersSealingAndStillCancelsClosedReviewWork()
    {
        var request = new Harness().Request with { PullRequestStatus = PrStatus.Completed };
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], []) { ObservedPullRequestStatus = PrStatus.Completed });
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetActiveJobsForConfigAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var sealer = Substitute.For<ICodeInsightMetricSealer>();
        var observer = Substitute.For<ICodeInsightCloseObserver>();
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            codeInsightMetricSealer: sealer, codeInsightCloseObserver: observer, discussions: discussions);
        await service.SynchronizeAsync(request);
        Assert.Empty(sealer.ReceivedCalls());
        Assert.Empty(observer.ReceivedCalls());
        await jobs.Received(1).GetActiveJobsForConfigAsync(
            request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverlappingCommentIdsFromAnotherProviderSourceDoNotClaimHumanOwnership()
    {
        var harness = new Harness();
        harness.Origins.GetJobIdsForPullRequestAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns([new PostedCommentOriginRow("1", "1", Guid.NewGuid())]);
        await harness.ObserveAsync();
        await harness.Origins.Received(1).GetOriginsForProviderSourceAsync(
            harness.Request.ClientId, harness.Request.RepositoryId,
            harness.Request.PullRequestId, harness.Request.Provider, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Origins.DidNotReceive().GetJobIdsForPullRequestAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await harness.Misses.Received(1).ObserveCurrentAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "1" && evt.Comments.All(comment => !comment.IsAiAuthored)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DelayedCloseEventCannotSealOrCancelAnAuthoritativelyReopenedPullRequest()
    {
        var request = new Harness().Request with { PullRequestStatus = PrStatus.Completed, ActivationSource = PullRequestActivationSource.Webhook };
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [])
                { ObservedPullRequestStatus = PrStatus.Active, CurrentConsumersRetained = true });
        var jobs = Substitute.For<IJobRepository>();
        var sealer = Substitute.For<ICodeInsightMetricSealer>();
        var threadPasses = Substitute.For<IThreadPassJobRepository>();
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            codeInsightMetricSealer: sealer, threadPassJobs: threadPasses, discussions: discussions);
        await service.SynchronizeAsync(request);
        Assert.Empty(jobs.ReceivedCalls());
        Assert.Empty(sealer.ReceivedCalls());
        Assert.Empty(threadPasses.ReceivedCalls());
    }

    [Fact]
    public async Task MissingPublishedFindingCannotCertifyFinalEvidenceOrCreateAHotDeletionRetry()
    {
        var harness = new Harness { Threads = [] };
        var scope = ProviderSourceIdentity.FromReviewSource(
            harness.Request.Provider, harness.Request.ProviderScopePath,
            LocalScmPolicies.Registry.GetSourceIdentityPolicy(harness.Request.Provider)).Value;
        harness.Findings =
        [
            new(
                Guid.NewGuid(), Guid.NewGuid(), "7", 0, "a.cs", 1, CommentSeverity.Warning, "Own finding", "1",
                DateTimeOffset.UtcNow, ProviderScope: scope, ProviderCommentId: "1")
        ];
        var observed = await harness.ObserveAsync();
        Assert.False(observed.CurrentConsumersRetained);
        Assert.False(harness.State.FullRefreshRequested);
        Assert.Equal(TimeSpan.FromSeconds(300), harness.State.NextAttemptAt - harness.State.LastObservedAt);
        var progress = harness.State.ReadProgress()["1"];
        Assert.False(progress.Consumers.ContainsKey("disposition"));
        await harness.Misses.Received(1).ObserveCurrentAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "1" && evt.IsDeleted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingNativeThreadIdentityKeepsCoverageIncompleteAtTheConfiguredPollingCadence()
    {
        var harness = new Harness { Threads = [new(null, "a.cs", 1, [new("Human", "Concern", CommentId: 1)], "Active")] };
        var observed = await harness.ObserveAsync();
        Assert.False(observed.CurrentConsumersRetained);
        Assert.False(harness.State.FullRefreshRequested);
        Assert.Equal(harness.State.RequestedGeneration, harness.State.CompletedGeneration);
        Assert.Equal(TimeSpan.FromSeconds(300), harness.State.NextAttemptAt - harness.State.LastObservedAt);
        await harness.Coverage.Received(1).RecordCurrentAcknowledgedAsync(
            Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), false,
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), false);
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnverifiableLegacyOriginDoesNotCertifyAnOwnCommentAsHuman()
    {
        var harness = new Harness();
        harness.Origins.GetOriginsForProviderSourceAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ScmProvider>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(new PostedCommentOriginSourceSnapshot([], [new("1", "1", Guid.NewGuid())]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ObserveAsync());
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetainedScopedReceiptPreservesOwnershipAfterIdentityRotationAndOriginPurge()
    {
        var harness = new Harness { ProjectReviewerOwnership = true };
        var scope = ProviderSourceIdentity.FromReviewSource(
            harness.Request.Provider, harness.Request.ProviderScopePath,
            LocalScmPolicies.Registry.GetSourceIdentityPolicy(harness.Request.Provider)).Value;
        var job = Guid.NewGuid();
        harness.Findings =
        [
            new(
                Guid.NewGuid(), job, "7", 0, "a.cs", 1, CommentSeverity.Warning, "Own finding", "1",
                DateTimeOffset.UtcNow, ProviderScope: scope, ProviderCommentId: "1"),
            new(
                Guid.NewGuid(), Guid.NewGuid(), "7", 0, "b.cs", 2, CommentSeverity.Warning, "Other source", "2",
                DateTimeOffset.UtcNow, ProviderScope: "foreign-scope", ProviderCommentId: "2")
        ];
        var observed = await harness.ObserveAsync();
        Assert.Equal("1", Assert.Single(observed.ReviewerThreads).ThreadId);
        await harness.Misses.Received(1).ObserveCurrentAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "1" && evt.Comments[0].IsAiAuthored &&
                                              evt.Comments[0].OriginatingJobId == job), Arg.Any<CancellationToken>());
        await harness.Misses.Received(1).ObserveCurrentAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "2" && !evt.Comments[0].IsAiAuthored), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedMemoryCollectionRemainsPendingUntilPositiveAcknowledgment()
    {
        var harness = new Harness
            { EnableMemory = true, ReviewerThreads = [new("1", "Fixed", "a.cs", "Concern", CodeChangedSinceRaised: ThreadAnchorCodeChange.Changed)] };
        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(false);
        await harness.ObserveAsync();
        Assert.False(harness.State.ReadProgress()["1"].Consumers.ContainsKey("memory"));
        Assert.Empty(harness.Scan.Threads);
        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        await harness.ObserveAsync();
        Assert.True(harness.State.ReadProgress()["1"].Consumers.ContainsKey("memory"));
        await harness.Memory.Received(2).ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolvedThreadReevaluatesMemoryWhenCodeChangeEvidenceBecomesAvailable()
    {
        var harness = new Harness { EnableMemory = true, ReviewerThreads = [new("1", "Fixed", "a.cs", "Concern")] };
        await harness.ObserveAsync();
        Assert.Equal("Fixed", Assert.Single(harness.Scan.Threads).LastSeenStatus);
        harness.ReviewerThreads[0] = harness.ReviewerThreads[0] with { CodeChangedSinceRaised = ThreadAnchorCodeChange.Changed };
        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveResolvedAsync(
            Arg.Is<ThreadResolvedDomainEvent>(evt => evt.CodeChangedSinceRaised == ThreadAnchorCodeChange.Unknown), Arg.Any<CancellationToken>());
        await harness.Memory.Received(1).ObserveResolvedAsync(
            Arg.Is<ThreadResolvedDomainEvent>(evt => evt.CodeChangedSinceRaised == ThreadAnchorCodeChange.Changed), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletedReviewerThreadRemovesMemoryAndAcknowledgesItsTombstone()
    {
        var harness = new Harness { EnableMemory = true, ReviewerThreads = [new("1", "Fixed", "a.cs", "Concern")] };
        await harness.ObserveAsync();
        harness.Threads = [harness.Threads[1]];
        harness.ReviewerThreads = [];
        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveReopenedAsync(Arg.Is<ThreadReopenedDomainEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        Assert.Null(Assert.Single(harness.Scan.Threads).LastSeenStatus);
        Assert.True(harness.State.ReadProgress()["1"].Consumers.ContainsKey("memory"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedReviewerThreadRetriesMemoryRemovalAfterFailedOrDeferredProcessing(bool deferred)
    {
        var harness = CreateResolvedMemoryHarness();
        await harness.ObserveAsync();
        harness.Threads = [];
        harness.ReviewerThreads = [];
        harness.Memory.ObserveReopenedAsync(Arg.Any<ThreadReopenedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(false);

        await harness.CreateService().ObserveAsync(harness.Request with { DeferMemoryProcessing = deferred });
        Assert.True(harness.State.FullRefreshRequested);
        Assert.Empty(JsonSerializer.Deserialize<PrThreadStatusEntry[]>(harness.State.ReviewerSummariesJson)!);
        harness.Memory.ClearReceivedCalls();
        harness.Memory.ObserveReopenedAsync(Arg.Any<ThreadReopenedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(false);
        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveReopenedAsync(Arg.Is<ThreadReopenedDomainEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        Assert.True(harness.State.FullRefreshRequested);

        harness.Memory.ClearReceivedCalls();
        harness.Memory.ObserveReopenedAsync(Arg.Any<ThreadReopenedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveReopenedAsync(Arg.Is<ThreadReopenedDomainEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        Assert.False(harness.State.FullRefreshRequested);
        Assert.True(harness.State.ReadProgress()["1"].Consumers.ContainsKey("memory"));
    }

    [Fact]
    public async Task InterruptedInitialMemoryProcessingRemovesMemoryWhenTheThreadDisappears()
    {
        var harness = CreateResolvedMemoryHarness();
        using var cancellation = new CancellationTokenSource();
        harness.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<bool>(call.Arg<CancellationToken>());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.CreateService().ObserveAsync(harness.Request, cancellation.Token));
        harness.Threads = [];
        harness.ReviewerThreads = [];

        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveReopenedAsync(Arg.Is<ThreadReopenedDomainEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        Assert.True(harness.State.ReadProgress()["1"].Consumers.ContainsKey("memory"));
    }

    [Fact]
    public async Task TargetedPreparationFailurePreservesUnaffectedReviewerSummaries()
    {
        var harness = new Harness { ReviewerThreads = [new("1", "Active", "a.cs", "First"), new("2", "Fixed", "b.cs", "Second")] };
        await harness.ObserveAsync();
        harness.State.RequestedGeneration++;
        harness.State.PendingThreadIdsJson = "[\"1\"]";
        harness.Threads[0] = harness.Threads[0] with { Status = "Fixed" };
        harness.Misses.PrepareDiscussionBatchAsync(Arg.Any<CodeInsightDiscussionBatch>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("transient"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ObserveAsync());
        Assert.Contains(JsonSerializer.Deserialize<PrThreadStatusEntry[]>(harness.State.ReviewerSummariesJson)!, entry => entry.ThreadId == "2");
    }

    [Fact]
    public async Task RemovingAllCommentsFromAnExistingResolvedThreadRemovesMemory()
    {
        var harness = new Harness { EnableMemory = true, ReviewerThreads = [new("1", "Fixed", "a.cs", "Concern")] };
        await harness.ObserveAsync();
        harness.Threads[0] = harness.Threads[0] with { Comments = [harness.Threads[0].Comments[0] with { IsDeleted = true }] };
        harness.Memory.ClearReceivedCalls();
        await harness.ObserveAsync();
        await harness.Memory.Received(1).ObserveReopenedAsync(Arg.Is<ThreadReopenedDomainEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        await harness.Memory.DidNotReceive().ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscussionOnlyReadFailureStillCancelsAnAuthoritativelyClosedJob()
    {
        var request = new Harness().Request with { DiscussionOnly = true, AllowReviewSubmission = false };
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).ThrowsAsync(
            new DiscussionObservationUnavailableException(
                PrStatus.Completed,
                null, new InvalidOperationException("incomplete")));
        var job = new ReviewJob(
            Guid.NewGuid(), request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, request.RepositoryId, request.PullRequestId, 7);
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetActiveJobsForConfigAsync(request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, Arg.Any<CancellationToken>()).Returns([job]);
        var sealer = Substitute.For<ICodeInsightMetricSealer>();
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            discussions: discussions, codeInsightMetricSealer: sealer);
        await service.SynchronizeAsync(request);
        await jobs.Received(1).SetCancelledAsync(job.Id, Arg.Any<CancellationToken>());
        Assert.Empty(sealer.ReceivedCalls());
    }

    [Fact]
    public async Task DiscussionOnlyUnavailableRevisionCannotAdmitAThreadPassAtIterationOne()
    {
        var request = new Harness().Request with { DiscussionOnly = true, AllowReviewSubmission = false, CandidateIterationId = null };
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).Returns(new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [], false));
        var jobs = Substitute.For<IJobRepository>();
        var threadJobs = Substitute.For<IThreadPassJobRepository>();
        threadJobs.TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>()).Returns(new TryClaimThreadPassResult(true, null));
        var clients = Substitute.For<IClientRegistry>();
        clients.GetCommentResolutionBehaviorAsync(request.ClientId, Arg.Any<CancellationToken>()).Returns(CommentResolutionBehavior.Silent);
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetRegisteredCapabilities(ScmProvider.AzureDevOps).Returns([ReviewThreadCapabilities.Status]);
        var service = new PullRequestSynchronizationService(
            registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            discussions: discussions, threadPassJobs: threadJobs, clientRegistry: clients,
            threadStatusFetcher: Substitute.For<IReviewerThreadStatusFetcher>(), prScanRepository: Substitute.For<IReviewPrScanThreadStatusStore>());
        await service.SynchronizeAsync(request);
        await threadJobs.DidNotReceive().TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstObservationWithoutATargetLeaseDoesNotEstablishEmptyReviewerEvidence()
    {
        var harness = new Harness { LeaseUnavailable = true };
        var observation = await harness.ObserveAsync();
        Assert.False(observation.EnumerationComplete);
        Assert.NotNull(observation.ReadFailure);
        Assert.Null(observation.ObservedPullRequestStatus);
        Assert.Null(observation.ObservedReviewRevision);
    }

    [Fact]
    public async Task SameServiceDoesNotReuseAnObservationWhileANewerLeaseIsActive()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        Assert.True((await service.ObserveAsync(harness.Request)).CurrentConsumersRetained);
        harness.State.LeaseToken = Guid.NewGuid();
        harness.LeaseUnavailable = true;

        var observation = await service.ObserveAsync(harness.Request);

        Assert.False(observation.CurrentConsumersRetained);
    }

    [Fact]
    public async Task ClosingRequestReusesAnAuthoritativeAbandonedObservation()
    {
        var harness = new Harness();
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => LocalScmPolicies.Registry.GetReviewSourcePolicy(call.Arg<ScmProvider>()));
        var query = Substitute.For<ICodeReviewQueryService>();
        registry.GetCodeReviewQueryService(ScmProvider.AzureDevOps).Returns(query);
        query.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>())
            .Returns(call => new ReviewDiscoveryItemDto(
                harness.Request.Provider, call.Arg<CodeReviewRef>().Repository,
                call.Arg<CodeReviewRef>(), CodeReviewState.Closed, null, null, "Test", null, null, null));
        var service = harness.CreateService(registry: registry);
        var first = await service.ObserveAsync(harness.Request with { PullRequestStatus = PrStatus.Abandoned });

        var second = await service.ObserveAsync(harness.Request with { PullRequestStatus = PrStatus.Completed, DiscussionOnly = true });

        Assert.Same(first, second);
        Assert.Equal(PrStatus.Abandoned, second.ObservedPullRequestStatus);
        await query.Received(1).GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>());
    }

    [Theory]
    [InlineData("event")]
    [InlineData("collection")]
    [InlineData("retention")]
    public async Task SupersededCapturedGenerationCannotAcknowledgeCurrentConsumers(string change)
    {
        var harness = new Harness();
        var changed = false;
        harness.Misses.ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (!changed)
            {
                changed = true;
                if (change == "event")
                {
                    harness.State.RequestedGeneration++;
                }
                else if (change == "collection")
                {
                    harness.State.CollectionGeneration++;
                }
                else
                {
                    harness.State.RetentionGeneration++;
                }
            }

            return new CodeInsightEvidenceAcknowledgment(true);
        });
        Assert.False((await harness.ObserveAsync()).CurrentConsumersRetained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalDiscussionRefreshPreservesLegacyBlockAndClosureScope(bool closed)
    {
        var request = new Harness().Request with { DiscussionOnly = true, AllowReviewSubmission = false };
        var scan = new ReviewPrScan(Guid.NewGuid(), request.ClientId, "https://dev.azure.com/Org/", "Project", "Repo", request.PullRequestId, "7");
        var scans = Substitute.For<IReviewPrScanThreadStatusStore>();
        scans.FindByProviderCoordinatesAsync(
            request.ClientId, request.Provider, request.ProviderScopePath, request.ProviderProjectKey,
            request.RepositoryId, request.PullRequestId, Arg.Any<CancellationToken>()).Returns(scan);
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [])
            {
                ObservedPullRequestStatus = closed ? PrStatus.Completed : PrStatus.Active,
                ObservedReviewRevision = new ReviewRevision("head", "base", null, "7", null)
            });
        var jobs = Substitute.For<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), request.ClientId, scan.OrganizationUrl, scan.ProjectId, scan.RepositoryId, request.PullRequestId, 7);
        jobs.GetActiveJobsForConfigAsync(request.ClientId, scan.OrganizationUrl, scan.ProjectId, Arg.Any<CancellationToken>()).Returns([job]);
        var blocks = Substitute.For<IBlockedPullRequestStore>();
        blocks.IsBlockedAsync(request.ClientId, scan.OrganizationUrl, scan.ProjectId, scan.RepositoryId, request.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(true);
        var threadJobs = Substitute.For<IThreadPassJobRepository>();
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            discussions: discussions, prScanRepository: scans, blockedPullRequestStore: blocks, threadPassJobs: threadJobs);
        await service.SynchronizeAsync(request);
        if (closed)
        {
            await jobs.Received(1).SetCancelledAsync(job.Id, Arg.Any<CancellationToken>());
        }
        else
        {
            await blocks.Received(1).IsBlockedAsync(
                request.ClientId, scan.OrganizationUrl, scan.ProjectId, scan.RepositoryId, request.PullRequestId, Arg.Any<CancellationToken>());
        }

        await threadJobs.DidNotReceive().TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalDiscussionPoliciesFindAnExistingBlockOrActiveJobWithoutAScan(bool closed)
    {
        var request = new Harness().Request with { DiscussionOnly = true, AllowReviewSubmission = false };
        var repository = LocalScmPolicies.Registry.GetReviewSourcePolicy(request.Provider).CreateCapturedRepository(
            request.ProviderScopePath, request.RepositoryId, request.ProviderProjectKey, request.RepositoryId);
        request = request with { Repository = repository, CodeReview = new(repository, CodeReviewPlatformKind.PullRequest, "42", 42) };
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetRegisteredCapabilities(ScmProvider.AzureDevOps).Returns([ReviewThreadCapabilities.Status]);
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [])
            {
                ObservedPullRequestStatus = closed ? PrStatus.Completed : PrStatus.Active,
                ObservedReviewRevision = new ReviewRevision("head", "base", null, "7", null)
            });
        var jobs = Substitute.For<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), request.ClientId, "https://dev.azure.com/Org/", "Project", "Repo", 42, 7);
        jobs.FindActiveByProviderCoordinatesAsync(
            request.ClientId, request.Provider, request.ProviderScopePath, request.ProviderProjectKey,
            request.RepositoryId, request.PullRequestId, Arg.Any<CancellationToken>()).Returns([job]);
        var blocks = Substitute.For<IBlockedPullRequestStore>();
        blocks.IsBlockedByProviderCoordinatesAsync(
            request.ClientId, request.Provider, request.ProviderScopePath, request.ProviderProjectKey,
            request.RepositoryId, request.PullRequestId, Arg.Any<CancellationToken>()).Returns(true);
        var clients = Substitute.For<IClientRegistry>();
        clients.GetCommentResolutionBehaviorAsync(request.ClientId, Arg.Any<CancellationToken>()).Returns(CommentResolutionBehavior.Silent);
        var threadJobs = Substitute.For<IThreadPassJobRepository>();
        threadJobs.TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>()).Returns(new TryClaimThreadPassResult(true, null));
        var service = new PullRequestSynchronizationService(
            registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            discussions: discussions, blockedPullRequestStore: blocks, threadPassJobs: threadJobs, clientRegistry: clients,
            prScanRepository: Substitute.For<IReviewPrScanThreadStatusStore>(), threadStatusFetcher: Substitute.For<IReviewerThreadStatusFetcher>());
        await service.SynchronizeAsync(request);
        if (closed)
        {
            await jobs.Received(1).SetCancelledAsync(job.Id, Arg.Any<CancellationToken>());
        }

        await threadJobs.DidNotReceive().TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeCloseHandlesALoneThreadPassWithoutAScanOrFileJob(bool cancellationFails)
    {
        var request = new Harness().Request with { DiscussionOnly = true, AllowReviewSubmission = false };
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request, Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [])
                { ObservedPullRequestStatus = PrStatus.Completed });
        var threads = Substitute.For<IThreadPassJobRepository>();
        threads.CancelActiveByProviderCoordinatesAsync(
            request.ClientId, request.Provider, request.ProviderScopePath, request.ProviderProjectKey,
            request.RepositoryId, request.PullRequestId, Arg.Any<CancellationToken>()).Returns(_ => cancellationFails
            ? Task.FromException<int>(new InvalidOperationException("transient cancellation failure"))
            : Task.FromResult(1));
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, Substitute.For<IJobRepository>(),
            NullLogger<PullRequestSynchronizationService>.Instance, discussions: discussions, threadPassJobs: threads,
            prScanRepository: Substitute.For<IReviewPrScanThreadStatusStore>());
        if (cancellationFails)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(request));
        }
        else
        {
            Assert.Equal(PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs, (await service.SynchronizeAsync(request)).LifecycleDecision);
        }
    }

    [Fact]
    public async Task SameServiceRetriesOnlyConsumersWhoseRetentionFailed()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        harness.Misses.ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>())
            .Returns(call => new CodeInsightEvidenceAcknowledgment(call.Arg<ThreadUpdatedEvent>().ThreadId != "1"));
        Assert.False((await service.ObserveAsync(harness.Request)).CurrentConsumersRetained);
        harness.Misses.ClearReceivedCalls();
        harness.Misses.ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(new CodeInsightEvidenceAcknowledgment(true));

        Assert.True((await service.ObserveAsync(harness.Request)).CurrentConsumersRetained);

        await harness.Misses.Received(1).ObserveCurrentAsync(Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "1"), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().ObserveCurrentAsync(Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameServiceReusesSuccessfulCurrentObservationWithoutAnotherLease()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        var first = await service.ObserveAsync(harness.Request);
        harness.Store.ClearReceivedCalls();
        harness.Reader.ClearReceivedCalls();

        Assert.Same(first, await service.ObserveAsync(harness.Request));

        await harness.Store.DidNotReceive().TryAcquireAsync(
            Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await harness.Reader.DidNotReceive().ReadAsync(
            Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("event")]
    [InlineData("collection")]
    [InlineData("retention")]
    [InlineData("dependency")]
    [InlineData("revision")]
    [InlineData("cadence")]
    public async Task SameServiceDoesNotReuseAnObservationAfterDurableOrRequestChanges(string change)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var harness = new Harness();
        var service = harness.CreateService(clock);
        await service.ObserveAsync(harness.Request);
        harness.Reader.ClearReceivedCalls();
        var request = harness.Request;
        switch (change)
        {
            case "event":
                harness.State.RequestedGeneration++;
                harness.State.PendingThreadIdsJson = "[\"1\"]";
                break;
            case "collection": harness.State.CollectionGeneration++; break;
            case "retention": harness.State.RetentionGeneration++; break;
            case "dependency": harness.Version = "changed-dependency"; break;
            case "revision": request = request with { CandidateIterationId = 8 }; break;
            case "cadence": clock.Advance(TimeSpan.FromSeconds(301)); break;
        }

        await service.ObserveAsync(request);

        Assert.Contains(harness.Reader.ReceivedCalls(), call => call.GetMethodInfo().Name is "ReadAsync" or "ReadTargetedAsync");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryWithoutAnOperationalConnectionDoesNotRequestDiscussionWork(bool inactive)
    {
        var harness = new Harness();
        var service = harness.CreateService();
        if (inactive)
        {
            harness.Connections.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(harness.Connection with { IsActive = false });
        }
        else
        {
            harness.Connections.GetOperationalConnectionAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
                .Returns((ClientScmConnectionCredentialDto?)null);
        }

        await service.RequestFromDeliveryAsync(harness.Request, "1", Guid.NewGuid());

        await harness.Store.DidNotReceiveWithAnyArgs().RequestFromDeliveryAsync(default!, default, default);
    }

    [Fact]
    public async Task DeliveryRequestPropagatesPersistenceFailureForAnAvailableTarget()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        harness.Store.RequestFromDeliveryAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestFromDeliveryAsync(harness.Request, "1", Guid.NewGuid()));
    }

    [Fact]
    public async Task SnapshotFailurePreservesAuthoritativeClosureAndRevision()
    {
        var harness = new Harness();
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => LocalScmPolicies.Registry.GetReviewSourcePolicy(call.Arg<ScmProvider>()));
        var query = Substitute.For<ICodeReviewQueryService>();
        registry.GetCodeReviewQueryService(ScmProvider.AzureDevOps).Returns(query);
        var service = harness.CreateService(registry: registry);
        var revision = new ReviewRevision("head", "base", null, "8", null);
        query.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>())
            .Returns(call => new ReviewDiscoveryItemDto(
                harness.Request.Provider, call.Arg<CodeReviewRef>().Repository,
                call.Arg<CodeReviewRef>(), CodeReviewState.Merged, revision, null, "Test", null, null, null));
        harness.Reader.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("snapshot unavailable"));

        var failure = await Assert.ThrowsAsync<DiscussionObservationUnavailableException>(() =>
            service.ObserveAsync(harness.Request with { DiscussionOnly = true }));

        Assert.Equal(PrStatus.Completed, failure.PullRequestStatus);
        Assert.Equal(revision, failure.ReviewRevision);
    }

    [Fact]
    public async Task ClosingRequestCannotReuseAnActiveObservationWithoutReadingAuthoritativeState()
    {
        var harness = new Harness();
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => LocalScmPolicies.Registry.GetReviewSourcePolicy(call.Arg<ScmProvider>()));
        var query = Substitute.For<ICodeReviewQueryService>();
        registry.GetCodeReviewQueryService(ScmProvider.AzureDevOps).Returns(query);
        var service = harness.CreateService(registry: registry);
        await service.ObserveAsync(harness.Request);
        query.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>())
            .Returns(call => new ReviewDiscoveryItemDto(
                harness.Request.Provider, call.Arg<CodeReviewRef>().Repository,
                call.Arg<CodeReviewRef>(), CodeReviewState.Merged, null, null, "Test", null, null, null));

        var closed = await service.ObserveAsync(harness.Request with { PullRequestStatus = PrStatus.Completed });

        Assert.Equal(PrStatus.Completed, closed.ObservedPullRequestStatus);
        Assert.True(closed.CurrentConsumersRetained);
        await query.Received(1).GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>());
    }

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    [InlineData("snapshot", false)]
    [InlineData("snapshot", true)]
    [InlineData("miss", false)]
    [InlineData("miss", true)]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    public async Task LongObservationWorkRenewsItsLeaseAndStopsWhenOwnershipIsLost(string phase, bool loseOwnership)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var harness = new Harness();
        var lease = Substitute.For<IDiscussionSynchronizationLease>();
        lease.State.Returns(harness.State);
        lease.ObservedAt.Returns(clock.GetUtcNow());
        lease.CompletionIsCurrent.Returns(true);
        var renewals = 0;
        var expires = clock.GetUtcNow().AddMinutes(2);
        var replaced = false;
        lease.EnsureCurrentAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (replaced || clock.GetUtcNow() >= expires)
            {
                throw new InvalidOperationException("Lease lost");
            }

            expires = clock.GetUtcNow().AddMinutes(2);
            Interlocked.Increment(ref renewals);
            return Task.CompletedTask;
        });
        harness.LeaseFactory = () => lease;
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => LocalScmPolicies.Registry.GetReviewSourcePolicy(call.Arg<ScmProvider>()));
        var query = Substitute.For<ICodeReviewQueryService>();
        registry.GetCodeReviewQueryService(ScmProvider.AzureDevOps).Returns(query);
        var service = harness.CreateService(clock, registry);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task WaitAsync(CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        }

        var request = harness.Request;
        if (phase == "metadata")
        {
            request = request with { DiscussionOnly = true };
            query.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>())
                .Returns(async call =>
                {
                    await WaitAsync(call.Arg<CancellationToken>());
                    var review = call.Arg<CodeReviewRef>();
                    return (ReviewDiscoveryItemDto?)new ReviewDiscoveryItemDto(
                        request.Provider, review.Repository, review, CodeReviewState.Open, null, null, "Test", null, null, null);
                });
        }
        else if (phase == "snapshot")
        {
            harness.Reader.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await WaitAsync(call.Arg<CancellationToken>());
                    return new PullRequestThreadSnapshot(clock.GetUtcNow(), harness.Threads, []);
                });
        }
        else if (phase == "miss")
        {
            harness.Misses.ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await WaitAsync(call.Arg<CancellationToken>());
                return new CodeInsightEvidenceAcknowledgment(true);
            });
        }
        else
        {
            harness.Archive.HandleThreadsUpdatedAsync(
                Arg.Any<PullRequestRetentionKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<IReadOnlyList<RetainedThreadSnapshot>>(), Arg.Any<CancellationToken>()).Returns(call => WaitAsync(call.Arg<CancellationToken>()));
        }

        var running = service.ObserveAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var iteration = 0; iteration < 6; iteration++)
            {
                var before = Volatile.Read(ref renewals);
                clock.Advance(TimeSpan.FromSeconds(30));
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                while (Volatile.Read(ref renewals) <= before)
                {
                    await Task.Delay(5, wait.Token);
                }
            }

            Assert.False(running.IsCompleted);
            if (loseOwnership)
            {
                replaced = true;
                clock.Advance(TimeSpan.FromSeconds(30));
                await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
                await lease.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!, default, default, default, default, default, default);
            }
            else
            {
                release.TrySetResult();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                var completedRenewals = Volatile.Read(ref renewals);
                clock.Advance(TimeSpan.FromMinutes(3));
                await Task.Yield();
                Assert.Equal(completedRenewals, Volatile.Read(ref renewals));
            }
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await running;
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class Harness
    {
        public readonly DiscussionSynchronizationState State = new();
        public readonly IReviewArchiveIngestionService Archive = Substitute.For<IReviewArchiveIngestionService>();
        public readonly IPostedCommentOriginStore Origins = Substitute.For<IPostedCommentOriginStore>();
        public readonly IPullRequestThreadSnapshotReader Reader = Substitute.For<IPullRequestThreadSnapshotReader>();
        public readonly ICodeInsightDispositionService Dispositions = Substitute.For<ICodeInsightDispositionService>();
        public readonly ICodeInsightHarvestCoverageRecorder Coverage = Substitute.For<ICodeInsightHarvestCoverageRecorder>();
        public readonly IThreadMemoryService Memory = Substitute.For<IThreadMemoryService>();
        public readonly IReviewPrScanThreadStatusStore Scans = Substitute.For<IReviewPrScanThreadStatusStore>();
        public readonly ReviewPrScan Scan;
        public PrThreadStatusEntry[] ReviewerThreads = [];
        public CodeInsightFindingView[] Findings = [];
        public bool EnableMemory, ProjectReviewerOwnership, LeaseUnavailable;
        public readonly ICodeInsightMissHarvester Misses = Substitute.For<ICodeInsightMissHarvester>();
        public ICodeInsightModelConcernObserver? Models;

        public readonly PullRequestSynchronizationRequest Request = new()
        {
            ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "test", ClientId = Guid.NewGuid(),
            Provider = ScmProvider.AzureDevOps, ProviderScopePath = "https://dev.azure.com/org", ProviderProjectKey = "project",
            RepositoryId = "repo", PullRequestId = 42, PullRequestStatus = PrStatus.Active, CandidateIterationId = 7,
        };

        private readonly Guid _connectionId = Guid.NewGuid();
        public readonly IDiscussionSynchronizationStore Store = Substitute.For<IDiscussionSynchronizationStore>();
        public readonly IClientScmConnectionRepository Connections = Substitute.For<IClientScmConnectionRepository>();
        public Func<IDiscussionSynchronizationLease?>? LeaseFactory;

        public ClientScmConnectionDto Connection => new(
                this._connectionId, this.Request.ClientId, ScmProvider.AzureDevOps, this.Request.ProviderScopePath,
                ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            { StoreThreads = this.Retention };

        public PrCommentThread[] Threads =
        [
            new("1", "a.cs", 1, [new("Human", "Sensitive discussion", CommentId: 1)], "Active"),
            new("2", "b.cs", 2, [new("Human", "Other concern", CommentId: 2)], "Fixed")
        ];

        public bool Enabled = true, Retention = true, Complete = true;
        public string Version = "v1";
        public string MemoryVersion = "routing-v1";
        public int Checkpoints;

        public Harness()
        {
            this.Dispositions.ObserveCurrentAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>())
                .Returns(new CodeInsightEvidenceAcknowledgment(true));
            this.Coverage.RecordCurrentAcknowledgedAsync(
                    Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<bool>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
                .Returns(call => new CodeInsightEvidenceAcknowledgment(call.ArgAt<bool>(5)));
            this.Scan = new(
                Guid.NewGuid(), this.Request.ClientId, this.Request.ProviderScopePath, this.Request.ProviderProjectKey,
                this.Request.RepositoryId, this.Request.PullRequestId, "7");
            this.Scans.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(this.Scan);
            this.Scans.SetLastSeenStatusesAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                foreach (var (id, status) in call.Arg<IReadOnlyDictionary<string, string?>>())
                {
                    var thread = this.Scan.Threads.FirstOrDefault(row => row.ThreadId == id);
                    if (thread is null)
                    {
                        thread = new ReviewPrScanThread { ReviewPrScanId = this.Scan.Id, ThreadId = id };
                        this.Scan.Threads.Add(thread);
                    }

                    thread.LastSeenStatus = status;
                }

                return Task.CompletedTask;
            });
            this.Memory.ObserveResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Memory.ObserveReopenedAsync(Arg.Any<ThreadReopenedDomainEvent>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Misses.ObserveCurrentAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>()).Returns(new CodeInsightEvidenceAcknowledgment(true));
            this.Origins.GetOriginsForProviderSourceAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ScmProvider>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>()).Returns(new PostedCommentOriginSourceSnapshot([], []));
        }

        public Task<PullRequestThreadSnapshot> ObserveAsync() => this.CreateService().ObserveAsync(this.Request);

        public DiscussionSynchronizationService CreateService(TimeProvider? timeProvider = null, IScmProviderRegistry? registry = null)
        {
            var store = this.Store;
            store.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<CancellationToken>()).Returns(this.State);
            store.IsCurrentObservationAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
            store.TryAcquireAsync(
                    Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IDiscussionSynchronizationLease?>(
                    this.LeaseUnavailable ? null : this.LeaseFactory?.Invoke() ?? new Lease(this.State, timeProvider, () => this.Checkpoints++)));
            var connections = this.Connections;
            connections.GetOperationalConnectionAsync(this.Request.ClientId, Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
                .Returns(
                    new ClientScmConnectionCredentialDto(
                        this._connectionId, this.Request.ClientId, ScmProvider.AzureDevOps, this.Request.ProviderScopePath,
                        ScmAuthenticationKind.PersonalAccessToken, "Test", "synthetic", true));
            connections.GetByIdAsync(this.Request.ClientId, this._connectionId, Arg.Any<CancellationToken>()).Returns(call => this.Connection);
            var reader = this.Reader;
            reader.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var ownership = call.Arg<ThreadOwnershipResolver>();
                    ownership.ContributeIdentity(new ThreadOwnerIdentity(Guid.NewGuid()));
                    var projected = this.ProjectReviewerOwnership
                        ? this.Threads.Where(thread => ownership.OwnsThread(
                                new(
                                    thread.ThreadId,
                                    thread.Comments[0].CommentId.ToString(System.Globalization.CultureInfo.InvariantCulture), thread.Comments[0].AuthorId)))
                            .Select(thread => new PrThreadStatusEntry(thread.ThreadId, thread.Status!, thread.FilePath, "Own finding")).ToArray()
                        : this.ReviewerThreads;
                    return new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, this.Threads, projected, this.Complete);
                });
            reader.ReadTargetedAsync(
                    Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ThreadOwnershipResolver>(),
                    Arg.Any<CancellationToken>())
                .Returns(call => new PullRequestThreadSnapshot(
                        DateTimeOffset.UtcNow,
                        this.Threads.Where(thread => call.Arg<IReadOnlyList<string>>().Contains(thread.ThreadId!)).ToArray(), [], false)
                    { TargetThreadIds = call.Arg<IReadOnlyList<string>>() });
            var batches = Substitute.For<ICodeInsightDiscussionBatchFactory>();
            batches.CreateAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => this.Enabled
                ? new CodeInsightDiscussionBatch(call.ArgAt<CodeInsightPullRequestKey>(0), call.ArgAt<string>(1), this.Findings, this.Version)
                : null);
            var service = new DiscussionSynchronizationService(
                store, reader, connections, registry ?? LocalScmPolicies.Registry,
                NullLogger<DiscussionSynchronizationService>.Instance, Microsoft.Extensions.Options.Options.Create(new DiscussionSynchronizationOptions()),
                origins: this.Origins, archive: this.Archive, batches: batches, misses: this.Misses, coverage: this.Coverage, dispositions: this.Dispositions,
                memory: this.EnableMemory ? this.Memory : null, scans: this.EnableMemory ? this.Scans : null, timeProvider: timeProvider,
                modelConcerns: this.Models, aiVersions: this.CreateMemoryVersions());
            return service;
        }

        private IAiConfigurationVersionReader CreateMemoryVersions()
        {
            var versions = Substitute.For<IAiConfigurationVersionReader>();
            versions.ReadAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AiPurpose>>(), Arg.Any<CancellationToken>()).Returns(_ => this.MemoryVersion);
            return versions;
        }
    }

    private sealed class Lease(DiscussionSynchronizationState state, TimeProvider? timeProvider = null, Action? checkpoint = null)
        : IDiscussionSynchronizationLease
    {
        private readonly long _generation = state.RequestedGeneration, _collection = state.CollectionGeneration, _retention = state.RetentionGeneration;
        public bool CompletionIsCurrent { get; private set; }
        public DiscussionSynchronizationState State => state;
        public DateTimeOffset ObservedAt { get; } = (timeProvider ?? TimeProvider.System).GetUtcNow();
        public Task EnsureCurrentAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task CheckpointProgressAsync(string progressJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (state.CollectionGeneration != this._collection || state.RetentionGeneration != this._retention)
            {
                throw new InvalidOperationException("The discussion consumer policies changed.");
            }

            state.ThreadProgressJson = progressJson;
            checkpoint?.Invoke();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            string progressJson, string summaries, DateTimeOffset observedAt, bool completeEnumeration, bool allConsumersCurrent,
            bool closed, string? revision, TimeSpan interval, CancellationToken ct = default)
        {
            this.CompletionIsCurrent = state.RequestedGeneration == this._generation && state.CollectionGeneration == this._collection &&
                                       state.RetentionGeneration == this._retention;
            state.ThreadProgressJson = progressJson;
            state.ReviewerSummariesJson = summaries;
            state.LastObservedAt = observedAt;
            if (completeEnumeration)
            {
                state.LastFullReconciledAt = observedAt;
            }

            state.LastReviewRevision = revision;
            state.IsClosed = closed && allConsumersCurrent && this.CompletionIsCurrent;
            state.FullRefreshRequested = !allConsumersCurrent;
            state.NextAttemptAt = observedAt + (allConsumersCurrent ? interval : TimeSpan.FromSeconds(15));
            if (allConsumersCurrent)
            {
                state.CompletedGeneration = state.RequestedGeneration;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
