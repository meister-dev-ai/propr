// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.CodeInsights.Classification;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.Application.Features.Providers.Identity;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

public sealed class ReviewerPerformanceRefreshTests
{
    [Fact]
    public async Task AnOlderMissClassifierCompletionCannotReplaceANewerSettledObservation()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HumanMissJudgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = (HumanMissJudgementRequest)call[0];
            if (request.Discussion.Contains("Older", StringComparison.Ordinal))
            {
                started.TrySetResult();
                return release.Task;
            }

            return Task.FromResult<HumanMissJudgement?>(new(true, true, true, .9, "settled"));
        });
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread();
        await harvester.HandleThreadObservedAsync(evt);
        var older = harvester.HandleThreadObservedAsync(
            evt with
            {
                Status = "Active",
                Comments = [new("1", "Human", false, DateTimeOffset.UtcNow, "Older discussion about a race.")]
            });
        await started.Task;
        Assert.True(
            await harvester.HandleThreadObservedAsync(
                evt with { Comments = [new("1", "Human", false, DateTimeOffset.UtcNow, "Newest settled discussion about the race.")] }));
        release.TrySetResult(new(true, false, true, .8, "provisional"));
        Assert.True(await older);
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.True(miss.JudgedThreadResolved);
        Assert.True(miss.WasActedOn);
        Assert.Contains("Newest", miss.EncryptedDiscussion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCollectionReplayPreservesSuccessfulJudgementsForTheSameSource()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var success = new CodeInsightMissRecord(
            "thread", "src/Service.cs", 1, "A concurrency issue", true, true, true, .9, "human-v1", true, "concurrency", CodeInsightFindingQualifier.Missing,
            "types-v1", .9, SourceFingerprint: "same-source");
        Assert.True(await store.RecordMissAsync(key, success));
        Assert.True(
            await store.RejudgeMissAsync(
                key,
                success with
                {
                    JudgementFailed = true,
                    IsSubstantive = false,
                    WasActedOn = false,
                    IsInScope = false,
                    Confidence = null,
                    TypeMembership = "",
                    Qualifier = null,
                    DimensionClassifierVersion = null
                }));
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.False(miss.JudgementFailed);
        Assert.True(miss.CountsAsMiss);
        Assert.Equal("concurrency", miss.TypeMembership);
        Assert.Equal("types-v1", miss.DimensionClassifierVersion);
    }

    [Fact]
    public async Task ARepeatedHumanObservationAdvancesOrderingWithoutReclassifyingOrAcceptingADelayedChange()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var now = DateTimeOffset.UtcNow;
        var evt = HumanThread() with { ObservedAt = now };
        Assert.True(await harvester.HandleThreadObservedAsync(evt));
        var miss = await db.CodeInsightMisses.SingleAsync();
        var judgedAt = miss.LastJudgedAt;
        Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = now.AddSeconds(2) }));
        Assert.True(
            await harvester.HandleThreadObservedAsync(
                evt with
                {
                    Status = "Active",
                    ObservedAt = now.AddSeconds(1)
                }));
        Assert.True(miss.JudgedThreadResolved);
        Assert.True(miss.WasActedOn);
        Assert.Equal(judgedAt, miss.LastJudgedAt);
        Assert.Equal(now.AddSeconds(2).ToUnixTimeMilliseconds(), miss.SourceObservedAt!.Value.ToUnixTimeMilliseconds());
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADisabledGateCannotAcknowledgeRetentionOfAnEligibleHumanObservation()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var harvester = new CodeInsightMissHarvester(
            store, store, Substitute.For<IHumanMissClassifier>(), gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        Assert.False(await harvester.HandleThreadObservedAsync(HumanThread()));
        Assert.True(
            await harvester.HandleThreadObservedAsync(HumanThread() with { Comments = [new("1", "Bot", true, DateTimeOffset.UtcNow, "Reviewer finding")] }));
    }

    [Fact]
    public async Task NewlyRetainedReviewerOverlapExcludesACurrentMissWithoutRejudgingTheHuman()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread();
        Assert.True(await harvester.HandleThreadObservedAsync(evt));
        await store.MaterialiseFindingsAsync(
            new(evt.ClientId, evt.RepositoryId, evt.PullRequestId), Guid.NewGuid(), "revision", DateTimeOffset.UtcNow,
            [
                new(
                    0, evt.FilePath, evt.Line, CommentSeverity.Warning, "This race needs synchronization.", null, null, null, false, null, null,
                    "reviewer-thread", "1")
            ]);
        Assert.True(await harvester.HandleThreadObservedAsync(evt));
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.False(miss.CountsAsMiss);
        Assert.True(miss.IsSubstantive);
        Assert.True(miss.WasActedOn);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepeatedCurrentOutcomeAdvancesWatermarkWithoutReclassifyingOrAcceptingDelayedStatus()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            RepositoryId = "repo",
            PullRequestId = 1
        };
        var finding = new CodeInsightFinding
        {
            Id = Guid.NewGuid(),
            CodeInsightPullRequestId = pr.Id,
            ProviderThreadId = "thread",
            ProviderCommentId = "comment",
            JobId = Guid.NewGuid(),
            EncryptedMessage = "finding"
        };
        db.AddRange(pr, finding);
        await db.SaveChangesAsync();
        var store = new CodeInsightFindingStore(db, Codec());
        var classifier = Substitute.For<IDisregardedFindingClassifier>();
        classifier.ClassifierVersion.Returns("judge-v1");
        classifier.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DisregardedFindingJudgement(false, .9, "accepted"));
        var service = new CodeInsightDispositionService(
            store, store, classifier, OpenGate(), NullLogger<CodeInsightDispositionService>.Instance,
            performanceEvidence: new CodeInsightPerformanceEvidenceStore(db));
        var now = DateTimeOffset.UtcNow;
        var evt = new ThreadResolvedDomainEvent(
            client, "https://example.org", "project", "repo", 1, "thread", null, null, "Unchanged discussion", now, NativeStatus: "Fixed");
        await service.HandleThreadResolvedAsync(evt);
        await service.HandleThreadResolvedAsync(evt with { ObservedAt = now.AddSeconds(2) });
        await service.HandleThreadResolvedAsync(
            evt with
            {
                NativeStatus = "WontFix",
                ObservedAt = now.AddSeconds(1)
            });
        Assert.Equal("Fixed", finding.NativeStatus);
        Assert.Equal(now.AddSeconds(2), finding.OutcomeObservedAt);
        Assert.Equal(CodeInsightDisposition.Dismissed, finding.CurrentDisposition);
        await classifier.Received(1).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedDimensionsRecoverOnUnchangedSourceWithoutRepeatingSuccessfulHumanJudgement()
    {
        await using var db = Db();
        var codec = Codec();
        var store = new CodeInsightFindingStore(db, codec);
        var gate = OpenGate();
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "evidence"));
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("dimension-v1");
        types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(
            FindingClassificationResult.Unusable(),
            FindingClassificationResult.Classified(new(["concurrency"], [], CodeInsightFindingLevel.File, CodeInsightFindingQualifier.Missing, .9)));
        var taxonomy = Substitute.For<ICodeInsightTaxonomyService>();
        var harvester = new CodeInsightMissHarvester(
            store, store, human, gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance, types, taxonomy);
        var evt = HumanThread();
        await harvester.HandleThreadObservedAsync(evt);
        await harvester.HandleThreadObservedAsync(evt);
        await harvester.HandleThreadObservedAsync(evt);
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.Equal("concurrency", miss.TypeMembership);
        Assert.Equal("dimension-v1", miss.DimensionClassifierVersion);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await types.Received(2).ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
        types.ClassifierVersion.Returns("dimension-v2");
        await harvester.HandleThreadObservedAsync(evt);
        Assert.Equal("dimension-v2", miss.DimensionClassifierVersion);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DimensionRetriesAreBoundedPerSourceAndVersionAndReuseSuccessfulHumanJudgement()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "evidence"));
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("dimension-v1");
        types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(FindingClassificationResult.Unusable());
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance, types,
            Substitute.For<ICodeInsightTaxonomyService>());
        var evt = HumanThread();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Assert.True(await harvester.HandleThreadObservedAsync(evt));
        }

        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.True(miss.DimensionJudgementFailed);
        Assert.Equal(3, miss.FailedDimensionAttempts);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await types.Received(3).ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
        types.ClassifierVersion.Returns("dimension-v2");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await harvester.HandleThreadObservedAsync(evt);
        }

        Assert.Equal(3, miss.FailedDimensionAttempts);
        Assert.Equal("dimension-v2", miss.DimensionClassifierVersion);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await types.Received(6).ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
        await harvester.HandleThreadObservedAsync(
            evt with { Comments = [new("2", "Human", false, DateTimeOffset.UtcNow, "The new discussion identifies a second concurrency issue.")] });
        Assert.Equal(1, miss.FailedDimensionAttempts);
        await human.Received(2).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await types.Received(7).ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExhaustedOutcomeJudgementsRetryOnlyForChangedDiscussionCodeOrVersion()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            RepositoryId = "repo",
            PullRequestId = 1
        };
        var finding = new CodeInsightFinding
        {
            Id = Guid.NewGuid(),
            CodeInsightPullRequestId = pr.Id,
            ProviderThreadId = "thread",
            JobId = Guid.NewGuid(),
            EncryptedMessage = "finding"
        };
        db.AddRange(pr, finding);
        await db.SaveChangesAsync();
        var store = new CodeInsightFindingStore(db, Codec());
        var classifier = Substitute.For<IDisregardedFindingClassifier>();
        classifier.ClassifierVersion.Returns("judge-v1");
        classifier.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>()).Returns((DisregardedFindingJudgement?)null);
        var service = new CodeInsightDispositionService(
            store, store, classifier, OpenGate(), NullLogger<CodeInsightDispositionService>.Instance,
            performanceEvidence: new CodeInsightPerformanceEvidenceStore(db));
        var now = DateTimeOffset.UtcNow;
        var evt = new ThreadResolvedDomainEvent(
            client, "https://example.org", "project", "repo", 1, "thread", null, null, "Initial discussion", now, NativeStatus: "Fixed");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await service.HandleThreadResolvedAsync(evt with { ObservedAt = now.AddSeconds(attempt) });
        }

        Assert.Equal(3, finding.OutcomeJudgementAttempts);
        await classifier.Received(3).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        var changed = evt with
        {
            CommentHistory = "Changed discussion",
            ObservedAt = now.AddSeconds(10)
        };
        await service.HandleThreadResolvedAsync(changed);
        Assert.Equal(1, finding.OutcomeJudgementAttempts);
        classifier.ClassifierVersion.Returns("judge-v2");
        await service.HandleThreadResolvedAsync(changed with { ObservedAt = now.AddSeconds(11) });
        Assert.Equal(1, finding.OutcomeJudgementAttempts);
        await service.HandleThreadResolvedAsync(
            changed with
            {
                Intent = ThreadResolutionIntent.ClaimsFix,
                CodeChangedSinceRaised = ThreadAnchorCodeChange.Changed,
                ObservedAt = now.AddSeconds(12)
            });
        Assert.Equal(CodeInsightDisposition.Addressed, finding.CurrentDisposition);
        Assert.Equal(0, finding.OutcomeJudgementAttempts);
        Assert.Equal(CodeInsightDisposition.Dismissed, (await db.CodeInsightFindingDispositions.SingleAsync()).Disposition);
        await classifier.Received(5).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangedDiscussionRefreshesCurrentOutcomeWhileTheInitialDispositionStaysSealed()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            RepositoryId = "repo",
            PullRequestId = 1
        };
        var finding = new CodeInsightFinding
        {
            Id = Guid.NewGuid(),
            CodeInsightPullRequestId = pr.Id,
            ProviderThreadId = "thread",
            ProviderCommentId = "comment",
            JobId = Guid.NewGuid(),
            EncryptedMessage = "finding"
        };
        db.AddRange(pr, finding);
        await db.SaveChangesAsync();
        var store = new CodeInsightFindingStore(db, Codec());
        var classifier = Substitute.For<IDisregardedFindingClassifier>();
        classifier.ClassifierVersion.Returns("judge-v1");
        classifier.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(
            new DisregardedFindingJudgement(false, .9, "accepted"), new DisregardedFindingJudgement(true, .9, "wrong"));
        var service = new CodeInsightDispositionService(
            store, store, classifier, OpenGate(), NullLogger<CodeInsightDispositionService>.Instance,
            performanceEvidence: new CodeInsightPerformanceEvidenceStore(db));
        var now = DateTimeOffset.UtcNow;
        var evt = new ThreadResolvedDomainEvent(
            client, "https://example.org", "project", "repo", 1, "thread", null, null, "Initial human discussion", now, NativeStatus: "Fixed");
        await service.HandleThreadResolvedAsync(evt);
        await service.HandleThreadResolvedAsync(
            evt with
            {
                CommentHistory = "Human now demonstrates that this finding is wrong",
                ObservedAt = now.AddSeconds(1)
            });
        Assert.Equal(CodeInsightDisposition.FalsePositive, finding.CurrentDisposition);
        Assert.Equal(CodeInsightDisposition.Dismissed, (await db.CodeInsightFindingDispositions.SingleAsync()).Disposition);
        await classifier.Received(2).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TaxonomyFailuresDoNotConsumeDimensionModelAttempts()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("dimension-v1");
        types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(
            FindingClassificationResult.Classified(new(["concurrency"], [], CodeInsightFindingLevel.File, CodeInsightFindingQualifier.Missing, .9)));
        var taxonomy = Substitute.For<ICodeInsightTaxonomyService>();
        var unavailable = true;
        taxonomy.GetAssignableTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            unavailable ? throw new InvalidOperationException("Taxonomy unavailable") : Task.FromResult(new CodeInsightTaxonomyDto(1, [], [])));
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance, types, taxonomy);
        var evt = HumanThread();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.True(await harvester.HandleThreadObservedAsync(evt));
        }

        unavailable = false;
        Assert.True(await harvester.HandleThreadObservedAsync(evt));
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.Equal("concurrency", miss.TypeMembership);
        Assert.Equal(0, miss.FailedDimensionAttempts);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await types.Received(1).ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindingReadFailuresDoNotConsumeHumanModelAttempts()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var findings = Substitute.For<ICodeInsightFindingStore>();
        var unavailable = true;
        findings.GetFindingsForPullRequestAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CancellationToken>()).Returns(call =>
            unavailable ? throw new InvalidOperationException("Finding store unavailable") : Task.FromResult<IReadOnlyList<CodeInsightFindingView>>([]));
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var harvester = new CodeInsightMissHarvester(
            findings, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.False(await harvester.HandleThreadObservedAsync(evt));
        }

        unavailable = false;
        Assert.True(await harvester.HandleThreadObservedAsync(evt));
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.True(miss.CountsAsMiss);
        Assert.Equal(0, miss.FailedJudgementAttempts);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOwnFindingLookupDoesNotAcknowledgeACompletedMissOrReplaceItsJudgement(bool classifyDimensions)
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var gate = OpenGate();
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("types-v1");
        types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(
            FindingClassificationResult.Classified(new(["concurrency"], [], CodeInsightFindingLevel.File, CodeInsightFindingQualifier.Missing, .9)));
        var taxonomy = Substitute.For<ICodeInsightTaxonomyService>();
        taxonomy.GetAssignableTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new CodeInsightTaxonomyDto(1, [], []));
        var normal = new CodeInsightMissHarvester(
            store, store, human, gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance,
            classifyDimensions ? types : null, classifyDimensions ? taxonomy : null, projector);
        var evt = HumanThread() with
        {
            ObservedAt = DateTimeOffset.UtcNow,
            ProviderScope = "AzureDevOps:https://ado.example.test"
        };
        Assert.True(await normal.HandleThreadObservedAsync(evt));
        var original = await db.CodeInsightMisses.SingleAsync();
        var judgedAt = original.LastJudgedAt;
        var sourceObservedAt = original.SourceObservedAt;
        var fingerprint = original.SourceFingerprint;
        var discussion = original.EncryptedDiscussion;
        Assert.Equal(1, await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss && row.Outcome == "missActed").SumAsync(row => row.Count));
        await store.MaterialiseFindingsAsync(
            new(evt.ClientId, evt.RepositoryId, evt.PullRequestId), Guid.NewGuid(), "revision", evt.ObservedAt.Value,
            [
                new(
                    0, evt.FilePath, evt.Line, CommentSeverity.Warning, "This race needs synchronization.", null, null, null, false, null, null,
                    "own", "1", ProviderScope: evt.ProviderScope)
            ]);
        var unavailable = Substitute.For<ICodeInsightFindingStore>();
        unavailable.GetFindingsForPullRequestAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CodeInsightFindingView>>>(_ => throw new InvalidOperationException("Finding read failed"));
        var failed = new CodeInsightMissHarvester(
            unavailable, store, human, gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance,
            classifyDimensions ? types : null, classifyDimensions ? taxonomy : null, projector);

        Assert.False(await failed.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(1) }));

        var retained = await db.CodeInsightMisses.SingleAsync();
        Assert.True(retained.CountsAsMiss);
        Assert.False(retained.ExcludedAsOwnFinding);
        Assert.False(retained.JudgementFailed);
        Assert.Equal(judgedAt, retained.LastJudgedAt);
        Assert.Equal(sourceObservedAt, retained.SourceObservedAt);
        Assert.Equal(fingerprint, retained.SourceFingerprint);
        Assert.Equal(discussion, retained.EncryptedDiscussion);
        Assert.Equal(classifyDimensions ? "concurrency" : "", retained.TypeMembership);
        Assert.Equal(1, await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss && row.Outcome == "missActed").SumAsync(row => row.Count));

        Assert.True(await normal.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(2) }));

        Assert.True(retained.ExcludedAsOwnFinding);
        Assert.False(retained.CountsAsMiss);
        Assert.True(retained.IsSubstantive);
        Assert.True(retained.WasActedOn);
        Assert.Equal(0, await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss && row.Outcome == "missActed").SumAsync(row => row.Count));
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        if (classifyDimensions)
        {
            await types.Received(1).ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await types.DidNotReceive().ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("runtime")]
    [InlineData("client")]
    [InlineData("exception")]
    public async Task DimensionClassifierSetupFailuresKeepTheModelBudgetAvailableAfterRecovery(string failure)
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var unavailable = true;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"types":["concurrency"],"level":"file","qualifier":"missing","confidence":0.9}""")));
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(_ => unavailable && failure == "client"
            ? throw new InvalidOperationException("Chat client unavailable")
            : chat);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.InsightsClassification, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (unavailable && failure == "binding")
            {
                throw new AiPurposeBindingNotConfiguredException(AiPurpose.InsightsClassification);
            }

            if (unavailable && failure == "runtime")
            {
                throw new InvalidOperationException("Runtime unavailable");
            }

            return Task.FromResult(runtime);
        });
        using var classifier = new AiFindingTypeClassifier(resolver, Substitute.For<IModelUsageRecorder>(), NullLogger<AiFindingTypeClassifier>.Instance);
        IFindingTypeClassifier types = classifier;
        if (failure == "exception")
        {
            types = Substitute.For<IFindingTypeClassifier>();
            types.ClassifierVersion.Returns(classifier.ClassifierVersion);
            types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
                unavailable
                    ? throw new InvalidOperationException("Classifier unavailable before request")
                    : classifier.ClassifyAsync((FindingClassificationRequest)call[0], (CancellationToken)call[1]));
        }

        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var taxonomy = Substitute.For<ICodeInsightTaxonomyService>();
        taxonomy.GetAssignableTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            new CodeInsightTaxonomyDto(
                CodeInsightCoreTaxonomy.Version,
                CodeInsightCoreTaxonomy.All.Select(tag => new CodeInsightCoreTagDto(
                    tag.Slug, tag.DisplayName, tag.Definition, tag.Characteristic, tag.BehaviourChanging)).ToList(), []));
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance, types, taxonomy);
        var evt = HumanThread() with { ObservedAt = DateTimeOffset.UtcNow };

        for (var pass = 0; pass < 3; pass++)
        {
            Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(pass) }));
        }

        var failed = await db.CodeInsightMisses.SingleAsync();
        Assert.True(failed.DimensionJudgementFailed);
        Assert.Equal(0, failed.FailedDimensionAttempts);
        await chat.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        unavailable = false;
        Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(3) }));

        var recovered = await db.CodeInsightMisses.SingleAsync();
        Assert.False(recovered.DimensionJudgementFailed);
        Assert.Equal("concurrency", recovered.TypeMembership);
        Assert.Equal(0, recovered.FailedDimensionAttempts);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await chat.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("runtime")]
    [InlineData("client")]
    public async Task HumanClassifierSetupFailuresKeepTheModelBudgetAvailableAfterRecovery(string failure)
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var unavailable = true;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"isSubstantive":true,"wasActedOn":true,"isInScope":true,"confidence":0.9}""")));
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(_ => unavailable && failure == "client"
            ? throw new InvalidOperationException("Chat client unavailable")
            : chat);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.InsightsClassification, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (unavailable && failure == "binding")
            {
                throw new AiPurposeBindingNotConfiguredException(AiPurpose.InsightsClassification);
            }

            if (unavailable && failure == "runtime")
            {
                throw new InvalidOperationException("Runtime unavailable");
            }

            return Task.FromResult(runtime);
        });
        var classifier = new AiHumanMissClassifier(resolver, Substitute.For<IModelUsageRecorder>(), NullLogger<AiHumanMissClassifier>.Instance);
        var harvester = new CodeInsightMissHarvester(
            store, store, classifier, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread() with { ObservedAt = DateTimeOffset.UtcNow };

        for (var pass = 0; pass < 3; pass++)
        {
            Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(pass) }));
        }

        var failed = await db.CodeInsightMisses.SingleAsync();
        Assert.True(failed.JudgementFailed);
        Assert.Equal(0, failed.FailedJudgementAttempts);
        await chat.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        unavailable = false;
        Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(3) }));

        var recovered = await db.CodeInsightMisses.SingleAsync();
        Assert.False(recovered.JudgementFailed);
        Assert.True(recovered.CountsAsMiss);
        Assert.Equal(0, recovered.FailedJudgementAttempts);
        await chat.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("response")]
    [InlineData("usage")]
    public async Task FailedOrUnusableHumanModelRequestsConsumeTheBoundedRetryBudget(string failure)
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var chat = Substitute.For<IChatClient>();
        if (failure == "provider")
        {
            chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Provider unavailable"));
        }
        else
        {
            chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(
                    new ChatResponse(
                        new ChatMessage(
                            ChatRole.Assistant, failure == "response"
                                ? "Unusable judgement"
                                : """{"isSubstantive":true,"wasActedOn":true,"isInScope":true,"confidence":0.9}""")));
        }

        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chat);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.InsightsClassification, Arg.Any<CancellationToken>()).Returns(runtime);
        var usage = Substitute.For<IModelUsageRecorder>();
        if (failure == "usage")
        {
            usage.RecordAsync(Arg.Any<Guid>(), Arg.Any<IResolvedAiChatRuntime>(), Arg.Any<ChatResponse>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("Usage recorder unavailable"));
        }

        var classifier = new AiHumanMissClassifier(resolver, usage, NullLogger<AiHumanMissClassifier>.Instance);
        var harvester = new CodeInsightMissHarvester(
            store, store, classifier, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread() with { ObservedAt = DateTimeOffset.UtcNow };

        for (var pass = 0; pass < 5; pass++)
        {
            Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(pass) }));
        }

        var failed = await db.CodeInsightMisses.SingleAsync();
        Assert.True(failed.JudgementFailed);
        Assert.Equal(3, failed.FailedJudgementAttempts);
        Assert.False(failed.CountsAsMiss);
        await chat.Received(3).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DelayedHumanJudgementCannotClearCurrentReviewerOverlap()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        var release = new TaskCompletionSource<HumanMissJudgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread() with { ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-2) };
        Assert.True(await harvester.HandleThreadObservedAsync(evt));
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var delayed = harvester.HandleThreadObservedAsync(
            evt with
            {
                Status = "Closed",
                ObservedAt = evt.ObservedAt.Value.AddSeconds(2)
            });
        await started.Task;
        await store.MaterialiseFindingsAsync(
            new(evt.ClientId, evt.RepositoryId, evt.PullRequestId), Guid.NewGuid(), "revision", DateTimeOffset.UtcNow,
            [new(0, evt.FilePath, evt.Line, CommentSeverity.Warning, "This race needs synchronization.", null, null, null, false, null, null, "own", "1")]);
        Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(1) }));
        release.TrySetResult(new(true, true, true, .9, "accepted"));
        Assert.True(await delayed);
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.True(miss.ExcludedAsOwnFinding);
        Assert.False(miss.CountsAsMiss);
        Assert.True(miss.IsSubstantive);
    }

    [Fact]
    public async Task LaterAiAuthorshipExcludesRetainedHumanMissAndKeepsItsJudgement()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread();
        Assert.True(await harvester.HandleThreadObservedAsync(evt));
        Assert.True(
            await harvester.HandleThreadObservedAsync(
                evt with { Comments = [.. evt.Comments, new("2", "Bot", true, DateTimeOffset.UtcNow, "Reviewer reply")] }));
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.True(miss.ExcludedAsOwnFinding);
        Assert.False(miss.CountsAsMiss);
        Assert.True(miss.IsSubstantive);
        Assert.True(miss.WasActedOn);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectionEditDuringClassificationKeepsTheObservedProviderNamespace()
    {
        await using var db = Db();
        var evt = HumanThread();
        var connection = new ClientScmConnectionRecord
        {
            Id = evt.ConnectionId,
            ClientId = evt.ClientId,
            Provider = ScmProvider.AzureDevOps,
            HostBaseUrl = "https://ado.example.test/collection"
        };
        db.ClientScmConnections.Add(connection);
        await db.SaveChangesAsync();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HumanMissJudgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var observation = harvester.HandleThreadObservedAsync(evt);
        await started.Task;
        connection.HostBaseUrl = "https://other.example.test/collection";
        await db.SaveChangesAsync();
        release.TrySetResult(new(true, true, true, .9, "accepted"));
        Assert.True(await observation);
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.Equal("AzureDevOps:https://ado.example.test/collection", miss.ProviderScope);
    }

    [Fact]
    public async Task FailedSameSourceReplayPreservesRawJudgementAndReconcilesCurrentOverlap()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var now = DateTimeOffset.UtcNow;
        var success = new CodeInsightMissRecord(
            "human", "src/Service.cs", 1, "Human: This race needs synchronization.", true, true, true, .9, "human-v1", true,
            SourceFingerprint: "same", SourceObservedAt: now);
        Assert.True(await store.RecordMissAsync(key, success));
        await store.MaterialiseFindingsAsync(
            key, Guid.NewGuid(), "revision", now,
            [
                new(
                    0, success.FilePath, success.LineNumber, CommentSeverity.Warning, "This race needs synchronization.", null, null, null, false, null, null,
                    "own", "1")
            ]);
        Assert.True(
            await store.RejudgeMissAsync(
                key, success with
                {
                    JudgementFailed = true,
                    SourceObservedAt = now.AddSeconds(1)
                }));
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.True(miss.ExcludedAsOwnFinding);
        Assert.False(miss.CountsAsMiss);
        Assert.False(miss.JudgementFailed);
        Assert.True(miss.IsSubstantive);
        Assert.True(miss.WasActedOn);
    }

    [Fact]
    public async Task FailedOwnFindingReadPreservesIncompleteDimensionsAndExcludesOverlapAfterRecovery()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var gate = OpenGate();
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("types-v1");
        types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(FindingClassificationResult.Unusable());
        var taxonomy = Substitute.For<ICodeInsightTaxonomyService>();
        var normal = new CodeInsightMissHarvester(
            store, store, human, gate, TestPostedCommentComposer.Default,
            NullLogger<CodeInsightMissHarvester>.Instance, types, taxonomy, projector);
        var evt = HumanThread() with { ObservedAt = DateTimeOffset.UtcNow };
        Assert.True(await normal.HandleThreadObservedAsync(evt));
        Assert.Equal(1, await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss && row.Outcome == "missActed").SumAsync(row => row.Count));
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        await store.MaterialiseFindingsAsync(
            key, Guid.NewGuid(), "revision", DateTimeOffset.UtcNow,
            [new(0, evt.FilePath, evt.Line, CommentSeverity.Warning, "This race needs synchronization.", null, null, null, false, null, null, "own", "1")]);
        var unavailable = Substitute.For<ICodeInsightFindingStore>();
        unavailable.GetFindingsForPullRequestAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CodeInsightFindingView>>>(_ => throw new InvalidOperationException("Finding read failed"));
        var failed = new CodeInsightMissHarvester(
            unavailable, store, human, gate, TestPostedCommentComposer.Default,
            NullLogger<CodeInsightMissHarvester>.Instance, types, taxonomy, projector);
        Assert.False(await failed.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(1) }));
        Assert.Equal(1, await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss && row.Outcome == "missActed").SumAsync(row => row.Count));
        Assert.True(await normal.HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(2) }));
        Assert.Equal(0, await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss && row.Outcome == "missActed").SumAsync(row => row.Count));
        Assert.True((await db.CodeInsightMisses.SingleAsync()).IsSubstantive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAiExclusionDoesNotAcknowledgeCompleteEnumeration(bool failWrite)
    {
        var misses = Substitute.For<ICodeInsightMissStore>();
        misses.ObserveThreadEligibilityAsync(
                Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new CodeInsightThreadEligibilityObservation((DateTimeOffset)call[4], (bool)call[3]));
        misses.GetObservationAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(call => failWrite
                ? Task.FromResult<CodeInsightMissObservation?>(new("source", false, 0))
                : throw new InvalidOperationException("Eligibility read failed"));
        misses.ObserveUnchangedMissAsync(
                Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns<Task<CodeInsightMissAcknowledgment>>(_ => throw new InvalidOperationException("Eligibility write failed"));
        var harvester = new CodeInsightMissHarvester(
            Substitute.For<ICodeInsightFindingStore>(), misses, Substitute.For<IHumanMissClassifier>(),
            OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread() with { Comments = [new("1", "Bot", true, DateTimeOffset.UtcNow, "Reviewer reply")] };
        Assert.False(await harvester.HandleThreadObservedAsync(evt));
    }

    [Fact]
    public async Task NewerAiObservationOrdersPendingFirstHumanClassificationWithoutInventingAMiss()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var gate = OpenGate();
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HumanMissJudgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var harvester = new CodeInsightMissHarvester(
            store, store, human, gate, TestPostedCommentComposer.Default,
            NullLogger<CodeInsightMissHarvester>.Instance, performanceProjector: projector);
        var evt = HumanThread() with
        {
            ObservedAt = DateTimeOffset.UtcNow,
            ProviderScope = "AzureDevOps:https://ado.example.test/tfs/collection"
        };
        var delayed = harvester.HandleThreadObservedAsync(evt);
        await started.Task;
        var ai = evt with
        {
            ObservedAt = evt.ObservedAt.Value.AddSeconds(1),
            Comments = [.. evt.Comments, new("2", "Bot", true, DateTimeOffset.UtcNow, "Reviewer reply")]
        };
        Assert.True(await harvester.HandleThreadObservedAsync(ai));
        Assert.Empty(await store.GetMissesForPullRequestAsync(new(evt.ClientId, evt.RepositoryId, evt.PullRequestId)));
        release.TrySetResult(new(true, true, true, .9, "accepted"));
        Assert.True(await delayed);
        await projector.ProjectAsync(new(evt.ClientId, evt.RepositoryId, evt.PullRequestId));
        Assert.Empty(await db.CodeInsightMisses.ToListAsync());
        Assert.Empty(await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss).ToListAsync());
    }

    [Fact]
    public async Task PendingNewerHumanObservationDoesNotAcknowledgeAnOlderFirstJudgement()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        var releases = new[]
        {
            new TaskCompletionSource<HumanMissJudgement?>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<HumanMissJudgement?>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var starts = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var index = 0;
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var current = index++;
            starts[current].TrySetResult();
            return releases[current].Task;
        });
        var harvester = new CodeInsightMissHarvester(
            store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = HumanThread() with { ObservedAt = DateTimeOffset.UtcNow };
        var older = harvester.HandleThreadObservedAsync(evt);
        await starts[0].Task;
        var newer = harvester.HandleThreadObservedAsync(
            evt with
            {
                ObservedAt = evt.ObservedAt.Value.AddSeconds(1),
                Status = "Closed"
            });
        await starts[1].Task;
        releases[0].TrySetResult(new(true, true, true, .9, "older"));
        Assert.False(await older);
        Assert.Empty(await db.CodeInsightMisses.ToListAsync());
        releases[1].TrySetResult(new(true, true, true, .9, "newer"));
        Assert.True(await newer);
        Assert.True((await db.CodeInsightMisses.SingleAsync()).CountsAsMiss);
    }

    [Theory]
    [InlineData("https://ado.example.test", "https://ado.example.test/collection")]
    [InlineData("https://ado.example.test/tfs", "https://ado.example.test/tfs/defaultcollection")]
    public async Task CapturedNestedCollectionMatchesOwnFindingsDispositionsAndHarvestCoverage(string connectionBase, string collection)
    {
        await using var db = Db();
        var evt = HumanThread();
        db.ClientScmConnections.Add(
            new ClientScmConnectionRecord
            {
                Id = evt.ConnectionId,
                ClientId = evt.ClientId,
                Provider = ScmProvider.AzureDevOps,
                HostBaseUrl = connectionBase
            });
        await db.SaveChangesAsync();
        var scope = ProviderSourceIdentity.FromReviewJob(
            new ReviewJob(Guid.NewGuid(), evt.ClientId, collection, "project", evt.RepositoryId, (int)evt.PullRequestId, 1)).Value;
        Assert.Equal(scope, ProviderSourceIdentity.FromReviewSource(ScmProvider.AzureDevOps, collection).Value);
        var store = new CodeInsightFindingStore(db, Codec());
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        await store.MaterialiseFindingsAsync(
            key, Guid.NewGuid(), "revision", DateTimeOffset.UtcNow,
            [
                new(
                    0, evt.FilePath, evt.Line, CommentSeverity.Warning, "This race needs synchronization.", null, null, null, false, null, null, "own", "1",
                    ProviderScope: scope)
            ]);
        var human = Substitute.For<IHumanMissClassifier>();
        var gate = OpenGate();
        var harvester = new CodeInsightMissHarvester(
            store, store, human, gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        Assert.True(await harvester.HandleThreadObservedAsync(evt with { ProviderScope = scope }));
        Assert.Empty(await store.GetMissesForPullRequestAsync(key));
        await human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        var dispositions = new CodeInsightDispositionService(
            store, store, Substitute.For<IDisregardedFindingClassifier>(), gate,
            NullLogger<CodeInsightDispositionService>.Instance, performanceEvidence: new CodeInsightPerformanceEvidenceStore(db));
        await dispositions.HandleThreadResolvedAsync(
            new(
                evt.ClientId, collection, "project", evt.RepositoryId, (int)evt.PullRequestId, "own", evt.FilePath,
                null, "Resolved concern", DateTimeOffset.UtcNow, NativeStatus: "Fixed", ProviderScope: scope));
        Assert.Equal("Fixed", (await db.CodeInsightFindings.SingleAsync()).NativeStatus);
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        await new CodeInsightHarvestCoverageRecorder(db, gate, projector, NullLogger<CodeInsightHarvestCoverageRecorder>.Instance)
            .RecordAsync(key, scope, true, DateTimeOffset.UtcNow);
        Assert.Contains(await db.ReviewerPerformanceDailyCounts.ToListAsync(), row => row.ProviderScope == scope && row.Outcome == "collectionComplete");
        Assert.DoesNotContain(await db.ReviewerPerformanceDailyCounts.ToListAsync(), row => row.Outcome == "collectionUnknown");
    }

    [Fact]
    public async Task EligibilityWatermarkPurgesWithItsClientWhilePreservingOtherClients()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var other = key with { ClientId = Guid.NewGuid() };
        var now = DateTimeOffset.UtcNow;
        await store.ObserveThreadEligibilityAsync(key, "thread", "scope", true, now);
        await store.ObserveThreadEligibilityAsync(other, "thread", "scope", true, now);
        Assert.Equal(1, await store.PurgeForClientAsync(key.ClientId));
        Assert.Null(await store.GetThreadEligibilityAsync(key, "thread", "scope"));
        Assert.NotNull(await store.GetThreadEligibilityAsync(other, "thread", "scope"));
        Assert.Empty(await db.CodeInsightMisses.ToListAsync());
    }

    [Fact]
    public async Task FailedCollectionAcknowledgmentProjectsAnExclusionFromANewerAiWatermark()
    {
        await using var db = Db();
        var store = new CodeInsightFindingStore(db, Codec());
        var gate = OpenGate();
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("types-v1");
        types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(FindingClassificationResult.Unusable());
        var taxonomy = Substitute.For<ICodeInsightTaxonomyService>();
        var evt = HumanThread() with
        {
            ObservedAt = DateTimeOffset.UtcNow,
            ProviderScope = "source"
        };
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        Assert.True(
            await new CodeInsightMissHarvester(
                store, store, human, gate, TestPostedCommentComposer.Default,
                NullLogger<CodeInsightMissHarvester>.Instance, types, taxonomy, projector).HandleThreadObservedAsync(evt));
        var findings = Substitute.For<ICodeInsightFindingStore>();
        findings.GetFindingsForPullRequestAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CodeInsightFindingView>>>(async _ =>
            {
                await store.ObserveThreadEligibilityAsync(key, evt.ThreadId, evt.ProviderScope, true, evt.ObservedAt.Value.AddSeconds(2));
                throw new InvalidOperationException("Finding read failed during newer AI observation");
            });
        Assert.True(
            await new CodeInsightMissHarvester(
                    findings, store, human, gate, TestPostedCommentComposer.Default,
                    NullLogger<CodeInsightMissHarvester>.Instance, types, taxonomy, projector)
                .HandleThreadObservedAsync(evt with { ObservedAt = evt.ObservedAt.Value.AddSeconds(1) }));
        Assert.True((await db.CodeInsightMisses.SingleAsync()).ExcludedAsOwnFinding);
        Assert.Equal(0, await db.ReviewerPerformanceDailyCounts.Where(row => row.IsMiss && row.Outcome == "missActed").SumAsync(row => row.Count));
    }

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ISecretProtectionCodec Codec()
    {
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(info => (string)info[0]);
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(info => (string)info[0]);
        return codec;
    }

    private static ICodeInsightsCollectionGate OpenGate()
    {
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return gate;
    }

    private static ThreadUpdatedEvent HumanThread() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.NewGuid(), "repo", 1, "thread", "src/Service.cs", 1, "Fixed", DateTimeOffset.UtcNow,
        [new("1", "Human", false, DateTimeOffset.UtcNow, "This race needs synchronization.")]);
}
