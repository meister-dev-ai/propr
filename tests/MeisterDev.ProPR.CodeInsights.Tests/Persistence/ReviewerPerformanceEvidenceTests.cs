// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using MeisterDev.ProPR.Application.Features.Providers.Identity;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

public sealed class ReviewerPerformanceEvidenceTests
{
    [Fact]
    public async Task AChangedPublicationReceiptClearsCurrentThreadEvidenceAndRetainsTheInitialDisposition()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var now = DateTimeOffset.UtcNow;
        var job = Guid.NewGuid();
        var input = new CodeInsightFindingSnapshot(
            0, "src/Service.cs", 1, CommentSeverity.Warning, "finding", null, null, null, false, null, null, "first-thread", "1",
            PublicationState: CodeInsightPublicationState.Published);
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [input]);
        var finding = await db.CodeInsightFindings.SingleAsync();
        var initial = new CodeInsightFindingDisposition
        {
            Id = Guid.NewGuid(),
            CodeInsightFindingId = finding.Id,
            Disposition = CodeInsightDisposition.Acknowledged,
            NativeStatus = "WontFix"
        };
        db.Add(initial);
        await db.SaveChangesAsync();
        await new CodeInsightPerformanceEvidenceStore(db).RecordCurrentOutcomeAsync(
            finding.Id, "WontFix", Outcome(), now, sourceFingerprint: "first-thread-evidence");
        var originalReceipt = new CodeInsightPublicationReceipt(finding.ProviderScope, finding.ProviderThreadId, finding.ProviderCommentId);
        await store.MaterialiseFindingsAsync(
            key, job, "revision", now, [
                input with
                {
                    ProviderThreadId = "second-thread",
                    ProviderCommentId = "2"
                }
            ]);
        Assert.Equal("Unknown", finding.NativeStatus);
        Assert.Null(finding.CurrentDisposition);
        Assert.Null(finding.OutcomeSourceFingerprint);
        Assert.Null(finding.OutcomeObservedAt);
        Assert.Equal("WontFix", initial.NativeStatus);
        Assert.False(
            await new CodeInsightPerformanceEvidenceStore(db).RecordCurrentOutcomeAsync(
                finding.Id, "Fixed", Outcome(), now.AddSeconds(1), receipt: originalReceipt));
        Assert.Equal("Unknown", finding.NativeStatus);
    }

    [Fact]
    public async Task ANewReceiptCannotInheritDuplicateVerificationFromThePreviousPublication()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var now = DateTimeOffset.UtcNow;
        var job = Guid.NewGuid();
        var input = new CodeInsightFindingSnapshot(
            0, null, null, CommentSeverity.Warning, "finding", null, null, null, false, null, null, "first-thread", "1",
            PublicationState: CodeInsightPublicationState.Published, DuplicateState: CodeInsightDuplicateState.ConfirmedDuplicate,
            DuplicateOfPublicationId: "original", DuplicateVerificationSource: "human-audit", DuplicateVerificationConfidence: .9, DuplicateVerifiedAt: now);
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [input]);
        await store.MaterialiseFindingsAsync(
            key, job, "revision", now,
            [
                input with
                {
                    ProviderThreadId = "new-thread",
                    DuplicateState = CodeInsightDuplicateState.Unknown,
                    DuplicateOfPublicationId = null,
                    DuplicateVerificationSource = null,
                    DuplicateVerificationConfidence = null,
                    DuplicateVerifiedAt = null
                }
            ]);
        var finding = await db.CodeInsightFindings.SingleAsync();
        Assert.Equal(CodeInsightDuplicateState.Unknown, finding.DuplicateState);
        Assert.Null(finding.DuplicateVerificationSource);
        Assert.Null(finding.DuplicateOfPublicationId);
        Assert.Null(finding.DuplicateVerifiedAt);
    }

    [Fact]
    public async Task NativeChangesAndReopeningRefreshCurrentEvidenceWithoutReplacingInitialDisposition()
    {
        await using var db = Db();
        var finding = Finding(Guid.NewGuid(), "scope-a");
        db.Add(finding);
        var initial = new CodeInsightFindingDisposition
        {
            Id = Guid.NewGuid(),
            CodeInsightFindingId = finding.Id,
            Disposition = CodeInsightDisposition.Acknowledged,
            NativeStatus = "WontFix"
        };
        db.Add(initial);
        await db.SaveChangesAsync();
        var store = new CodeInsightPerformanceEvidenceStore(db);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "WontFix", Outcome(), now));
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "ByDesign", Outcome(), now.AddSeconds(1)));
        Assert.Equal("ByDesign", finding.NativeStatus);
        Assert.Equal("WontFix", initial.NativeStatus);
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "Active", null, now.AddSeconds(2)));
        Assert.Null(finding.CurrentDisposition);
        Assert.Equal(CodeInsightDisposition.Acknowledged, initial.Disposition);
        Assert.False(await store.RecordCurrentOutcomeAsync(finding.Id, "ByDesign", Outcome(), now));
    }

    [Fact]
    public async Task SameNativeStatusRetainsLaterCorroborationAndRecoversFromFailedClassifierWithBoundedAttempts()
    {
        await using var db = Db();
        var finding = Finding(Guid.NewGuid(), "scope-a");
        db.Add(finding);
        await db.SaveChangesAsync();
        var store = new CodeInsightPerformanceEvidenceStore(db);
        var now = DateTimeOffset.UtcNow;
        var failed = new CodeInsightDispositionRecord(
            CodeInsightDisposition.Dismissed, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unknown, "judge-v1", null);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", failed, now.AddSeconds(i)));
        }

        Assert.Equal(3, finding.OutcomeJudgementAttempts);
        Assert.False(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", failed, now.AddSeconds(4)));
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", failed with { ClassifierConfidence = .9 }, now.AddSeconds(5)));
        Assert.Equal(.9, finding.CurrentClassifierConfidence);
        Assert.True(
            await store.RecordCurrentOutcomeAsync(
                finding.Id, "Fixed", new(CodeInsightDisposition.Addressed, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed, null, null),
                now.AddSeconds(6)));
        Assert.Equal(ThreadAnchorCodeChange.Changed, finding.CurrentCodeChange);
    }

    [Fact]
    public async Task ProviderQualifiedLookupRefusesAmbiguousUnqualifiedIdentities()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            RepositoryId = "1",
            PullRequestId = 1
        };
        db.Add(pr);
        var a = Finding(pr.Id, "GitHub:https://github.example");
        var b = Finding(pr.Id, "GitLab:https://gitlab.example");
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns("finding");
        var store = new CodeInsightFindingStore(db, codec);
        Assert.Null(await store.FindByProviderThreadAsync(client, "1", 1, "1"));
        Assert.Equal(a.Id, (await store.FindByProviderThreadAsync(client, "1", 1, "1", providerScope: a.ProviderScope))!.Id);
        await new CodeInsightPerformanceEvidenceStore(db).RecordCurrentOutcomeAsync(a.Id, "ByDesign", Outcome(), DateTimeOffset.UtcNow);
        Assert.Null(b.NativeStatus);
    }

    [Fact]
    public async Task KnownProviderEventsCannotClaimAnUnknownHistoricalSource()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            RepositoryId = "1",
            PullRequestId = 1
        };
        db.Add(pr);
        var finding = Finding(pr.Id, "");
        db.Add(finding);
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns("finding");
        var store = new CodeInsightFindingStore(db, codec);
        Assert.Null(await store.FindByProviderThreadAsync(client, "1", 1, "1", providerScope: "GitHub:https://other.example"));
        Assert.Equal(finding.Id, (await store.FindByProviderThreadAsync(client, "1", 1, "1"))!.Id);
    }

    [Fact]
    public void ProviderScopeRetainsProviderAndConfiguredBasePathWithoutCredentials()
    {
        Assert.Equal(
            "GitHub:https://github.example/base/path",
            ProviderSourceIdentity.FromConfiguredHost(ScmProvider.GitHub, "https://user:password@github.example/base/path/?token=secret#fragment").Value);
        Assert.NotEqual(
            ProviderSourceIdentity.FromConfiguredHost(ScmProvider.AzureDevOps, "https://dev.azure.com/orgA").Value,
            ProviderSourceIdentity.FromConfiguredHost(ScmProvider.AzureDevOps, "https://dev.azure.com/orgB").Value);
    }

    [Fact]
    public async Task DifferentProviderNamespacesRetainAndRefreshIndependentHumanThreadJudgements()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var key = new CodeInsightPullRequestKey(client, "1", 1);
        var a = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            Provider = ScmProvider.AzureDevOps,
            HostBaseUrl = "https://dev.azure.com/orgA"
        };
        var b = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            Provider = ScmProvider.AzureDevOps,
            HostBaseUrl = "https://dev.azure.com/orgB"
        };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var miss = new CodeInsightMissRecord(
            "1", "src/Service.cs", 1, "human concern", true, true, true, .9, "judge-v1", true, ConnectionId: a.Id, SourceFingerprint: "a");
        Assert.True(await store.RecordMissAsync(key, miss));
        Assert.True(
            await store.RecordMissAsync(
                key, miss with
                {
                    ConnectionId = b.Id,
                    SourceFingerprint = "b"
                }));
        Assert.False(await store.RecordMissAsync(key, miss));
        Assert.Null(await store.GetObservationAsync(key, "1"));
        Assert.Equal("a", (await store.GetObservationAsync(key, "1", connectionId: a.Id))!.SourceFingerprint);
        Assert.Equal("b", (await store.GetObservationAsync(key, "1", connectionId: b.Id))!.SourceFingerprint);
        Assert.True(
            await store.RejudgeMissAsync(
                key, miss with
                {
                    ConnectionId = b.Id,
                    WasActedOn = false,
                    SourceFingerprint = "updated-b"
                }));
        Assert.True((await db.CodeInsightMisses.SingleAsync(row => row.ProviderScope.EndsWith("orgA"))).WasActedOn);
        Assert.False((await db.CodeInsightMisses.SingleAsync(row => row.ProviderScope.EndsWith("orgB"))).WasActedOn);
    }

    [Fact]
    public async Task PublicationReplayEnrichesVerifiedDuplicatesAndChangedReceiptsWithoutErasingKnownEvidence()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var job = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var input = new CodeInsightFindingSnapshot(
            0, "src/Service.cs", 1, CommentSeverity.Warning, "finding", null, null, null, false, null, null, "first-thread", "first-comment",
            PublicationState: CodeInsightPublicationState.Published);
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [input]);
        var finding = await db.CodeInsightFindings.SingleAsync();
        var stamp = finding.PerformanceEvidenceUpdatedAt;
        var enriched = input with
        {
            ProviderCommentId = "new-comment",
            DuplicateState = CodeInsightDuplicateState.ConfirmedDuplicate,
            DuplicateVerificationSource = "human-audit",
            DuplicateVerificationConfidence = .9,
            DuplicateVerifiedAt = now.AddMinutes(1),
            DuplicateOfPublicationId = "known-publication"
        };
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [enriched]);
        Assert.Equal(CodeInsightDuplicateState.ConfirmedDuplicate, finding.DuplicateState);
        Assert.Equal("human-audit", finding.DuplicateVerificationSource);
        Assert.True(finding.PerformanceEvidenceUpdatedAt > stamp);
        stamp = finding.PerformanceEvidenceUpdatedAt;
        await store.MaterialiseFindingsAsync(
            key, job, "revision", now,
            [
                input with
                {
                    ProviderThreadId = null,
                    ProviderCommentId = null,
                    PublicationState = CodeInsightPublicationState.Unknown
                }
            ]);
        Assert.Equal("new-comment", finding.ProviderCommentId);
        Assert.Equal(CodeInsightDuplicateState.ConfirmedDuplicate, finding.DuplicateState);
        Assert.Equal(stamp, finding.PerformanceEvidenceUpdatedAt);
    }

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CodeInsightFinding Finding(Guid pr, string scope) => new()
    {
        Id = Guid.NewGuid(),
        CodeInsightPullRequestId = pr,
        JobId = Guid.NewGuid(),
        RevisionKey = Guid.NewGuid().ToString(),
        ProviderScope = scope,
        ProviderThreadId = "1",
        ProviderCommentId = "1"
    };

    private static CodeInsightDispositionRecord Outcome() => new(
        CodeInsightDisposition.Acknowledged, ThreadResolutionIntent.AcceptedByHuman, ThreadAnchorCodeChange.Unknown, null, null);
}
