// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

public sealed class CodeInsightFindingStoreTests : IDisposable
{
    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightFindingStore _store;

    public CodeInsightFindingStoreTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightFindingStoreTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new CodeInsightFindingStore(this._dbContext, CreateCodec());
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task MaterialiseFindingsAsync_AssignsSurrogateIdsAndRoundTripsEveryField()
    {
        var key = NewKey();
        var observedAt = DateTimeOffset.UtcNow;

        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", observedAt, NewFindings());

        var stored = await this._store.GetFindingsForPullRequestAsync(key);
        Assert.Equal(2, stored.Count);

        Assert.All(stored, finding => Assert.NotEqual(Guid.Empty, finding.Id));
        Assert.Equal(stored.Count, stored.Select(finding => finding.Id).Distinct().Count());

        Assert.Equal("src/Service.cs", stored[0].FilePath);
        Assert.Equal(42, stored[0].LineNumber);
        Assert.Equal(CommentSeverity.Error, stored[0].Severity);
        Assert.Equal("Null dereference", stored[0].Message);
        Assert.Equal("thread-1", stored[0].ProviderThreadId);
        Assert.Equal(JobId, stored[0].JobId);
        Assert.Equal("rev-1", stored[0].RevisionKey);

        // A pull-request-level finding has no anchor and no provider thread of its own.
        Assert.Null(stored[1].FilePath);
        Assert.Null(stored[1].LineNumber);
        Assert.Null(stored[1].ProviderThreadId);
    }

    [Fact]
    public async Task MaterialiseFindingsAsync_RecordsWhichModelProducedTheFindingAndLeavesItNullWhenUnknown()
    {
        // Both identities, because the configured name can be repointed at a different remote model, and a pass
        // that reported no model stays unattributed rather than inheriting another pass's label.
        var key = NewKey();

        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());

        var stored = await this._dbContext.CodeInsightFindings
            .OrderBy(finding => finding.Ordinal)
            .ToListAsync();

        Assert.Equal("gpt-5.4-mini", stored[0].OriginModelId);
        Assert.Equal("thrifty-reviewer", stored[0].OriginLogicalModelName);
        Assert.Null(stored[1].OriginModelId);
        Assert.Null(stored[1].OriginLogicalModelName);
    }

    [Fact]
    public async Task MaterialiseFindingsAsync_RecordsTheDefinitionTheFindingSitsInAndLeavesItNullWhenUnplaced()
    {
        // What makes findings countable per part of a codebase rather than only per file. A pull-request-level
        // finding sits in no definition, and must not borrow one.
        var key = NewKey();

        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());

        var stored = await this._dbContext.CodeInsightFindings
            .OrderBy(finding => finding.Ordinal)
            .ToListAsync();

        Assert.Equal("Process", stored[0].OriginSymbolName);
        Assert.Equal("Method", stored[0].OriginSymbolKind);
        Assert.Null(stored[1].OriginSymbolName);
        Assert.Null(stored[1].OriginSymbolKind);
    }

    [Fact]
    public async Task MaterialiseFindingsAsync_StoresTheMessageEncryptedAtRest()
    {
        var key = NewKey();

        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());

        var raw = await this._dbContext.CodeInsightFindings
            .Select(finding => finding.EncryptedMessage)
            .ToListAsync();

        Assert.All(raw, message => Assert.DoesNotContain("Null dereference", message, StringComparison.Ordinal));
        Assert.All(raw, message => Assert.NotEqual("Null dereference", message));
    }

    [Fact]
    public async Task MaterialiseFindingsAsync_ReprocessingTheSameIncrementIsIdempotentAndKeepsIds()
    {
        var key = NewKey();
        var observedAt = DateTimeOffset.UtcNow;

        var firstCreated = await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", observedAt, NewFindings());
        var firstIds = (await this._store.GetFindingsForPullRequestAsync(key))
            .Select(finding => finding.Id)
            .ToList();

        var secondCreated = await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", observedAt, NewFindings());
        var secondIds = (await this._store.GetFindingsForPullRequestAsync(key))
            .Select(finding => finding.Id)
            .ToList();

        Assert.Equal(2, firstCreated);
        Assert.Equal(0, secondCreated);
        // Re-materialising must not hand out new surrogates: every tag, disposition, and memory link already
        // pointing at the old ids would otherwise be orphaned.
        Assert.Equal(firstIds, secondIds);
    }

    [Fact]
    public async Task MaterialiseFindingsAsync_ASecondIncrementAddsItsOwnFindings()
    {
        var key = NewKey();
        var observedAt = DateTimeOffset.UtcNow;

        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", observedAt, NewFindings());
        await this._store.MaterialiseFindingsAsync(key, Guid.NewGuid(), "rev-2", observedAt, NewFindings());

        var stored = await this._store.GetFindingsForPullRequestAsync(key);
        Assert.Equal(4, stored.Count);
        Assert.Equal(2, stored.Count(finding => finding.RevisionKey == "rev-1"));
        Assert.Equal(2, stored.Count(finding => finding.RevisionKey == "rev-2"));
    }

    [Fact]
    public async Task MaterialiseFindingsAsync_ARepostFillsInAProviderThreadWithoutErasingAKnownOne()
    {
        var key = NewKey();
        var observedAt = DateTimeOffset.UtcNow;

        // First pass: the anchored finding was posted, the pull-request-level one was not.
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", observedAt, NewFindings());

        // Second pass posts nothing at all; a known join key must survive it.
        var unposted = NewFindings()
            .Select(finding => finding with { ProviderThreadId = null, ProviderCommentId = null })
            .ToList();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", observedAt, unposted);

        var stored = await this._store.GetFindingsForPullRequestAsync(key);
        Assert.Equal("thread-1", stored[0].ProviderThreadId);
    }

    [Fact]
    public async Task FindByProviderThreadAsync_ReturnsTheFindingAndNullForAnUnknownThread()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());

        var match = await this._store.FindByProviderThreadAsync(key.ClientId, key.RepositoryId, key.PullRequestId, "thread-1");
        var miss = await this._store.FindByProviderThreadAsync(key.ClientId, key.RepositoryId, key.PullRequestId, "thread-unknown");

        Assert.NotNull(match);
        Assert.Equal("Null dereference", match.Message);
        // A thread raised before collection was enabled has no record; the caller must skip, not invent one.
        Assert.Null(miss);
    }

    [Fact]
    public async Task FindByProviderThreadAsync_DoesNotLeakAcrossClients()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());

        var otherClient = await this._store.FindByProviderThreadAsync(Guid.NewGuid(), key.RepositoryId, key.PullRequestId, "thread-1");

        Assert.Null(otherClient);
    }

    [Fact]
    public async Task TouchPullRequestAsync_MovesTheRetentionAnchorForwardOnly()
    {
        var key = NewKey();
        var later = DateTimeOffset.UtcNow;
        var earlier = later.AddDays(-5);

        await this._store.TouchPullRequestAsync(key, "Active", later);
        await this._store.TouchPullRequestAsync(key, "Completed", earlier);

        var aggregate = await this._dbContext.CodeInsightPullRequests.SingleAsync();
        // A late-arriving older observation must not shorten the window the aggregate has left.
        Assert.Equal(later, aggregate.LastActivityAt);
        Assert.Equal("Completed", aggregate.PullRequestState);
    }

    [Fact]
    public async Task TouchPullRequestAsync_RecordsTheRepositoryDisplayNameAndKeepsItWhenALaterTouchHasNone()
    {
        // The provider's identifier is what everything keys on, but for several providers it is a bare number, so
        // the display name is recorded alongside it. A caller that does not know a name (the human-thread harvest)
        // must not erase the one a review reported.
        var key = NewKey();

        await this._store.TouchPullRequestAsync(key, "Active", DateTimeOffset.UtcNow, "Payments API");
        await this._store.TouchPullRequestAsync(key, "Completed", DateTimeOffset.UtcNow);

        var aggregate = await this._dbContext.CodeInsightPullRequests.SingleAsync();
        Assert.Equal("Payments API", aggregate.RepositoryName);
    }

    [Fact]
    public async Task TouchPullRequestAsync_ARenamedRepositoryCatchesUpOnItsNextReview()
    {
        var key = NewKey();

        await this._store.TouchPullRequestAsync(key, "Active", DateTimeOffset.UtcNow, "old-name");
        await this._store.TouchPullRequestAsync(key, "Active", DateTimeOffset.UtcNow, "new-name");

        var aggregate = await this._dbContext.CodeInsightPullRequests.SingleAsync();
        Assert.Equal("new-name", aggregate.RepositoryName);
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesExpiredAggregatesWithTheirFindingsAndKeepsFreshOnes()
    {
        var expiredKey = NewKey();
        var freshKey = new CodeInsightPullRequestKey(expiredKey.ClientId, "repo-2", 200);
        var now = DateTimeOffset.UtcNow;

        await this._store.MaterialiseFindingsAsync(expiredKey, JobId, "rev-1", now.AddDays(-40), NewFindings());
        await this._store.MaterialiseFindingsAsync(freshKey, JobId, "rev-1", now, NewFindings());

        var removed = await this._store.PurgeExpiredAsync(now.AddDays(-30));

        Assert.Equal(1, removed);
        Assert.Empty(await this._store.GetFindingsForPullRequestAsync(expiredKey));
        Assert.Equal(2, (await this._store.GetFindingsForPullRequestAsync(freshKey)).Count);
        Assert.Single(await this._dbContext.CodeInsightPullRequests.ToListAsync());
        Assert.Equal(2, await this._dbContext.CodeInsightFindings.CountAsync());
    }

    [Fact]
    public async Task PurgeForClientAsync_RemovesEverythingForThatClientOnly()
    {
        var key = NewKey();
        var otherClientKey = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo-1", 100);
        var now = DateTimeOffset.UtcNow;

        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", now, NewFindings());
        await this._store.MaterialiseFindingsAsync(otherClientKey, JobId, "rev-1", now, NewFindings());

        var removed = await this._store.PurgeForClientAsync(key.ClientId);

        Assert.Equal(1, removed);
        Assert.Empty(await this._store.GetFindingsForPullRequestAsync(key));
        Assert.Equal(2, (await this._store.GetFindingsForPullRequestAsync(otherClientKey)).Count);
    }

    [Fact]
    public async Task GetFindingsForPullRequestAsync_ReturnsEmptyForAnUncollectedPullRequest()
    {
        Assert.Empty(await this._store.GetFindingsForPullRequestAsync(NewKey()));
    }

    [Fact]
    public async Task ListUnclassifiedAsync_ReturnsCollectedFindingsWithTheirClientAndDecryptedText()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());

        var pending = await this._store.ListUnclassifiedAsync(limit: 10, maxAttempts: 3);

        Assert.Equal(2, pending.Count);
        // The classifier needs the client (for its model, vocabulary, and gate) and the plain text.
        Assert.All(pending, finding => Assert.Equal(key.ClientId, finding.ClientId));
        Assert.Contains(pending, finding => finding.Message == "Null dereference");
        Assert.All(pending, finding => Assert.Equal(0, finding.Attempts));
    }

    [Fact]
    public async Task ListUnclassifiedAsync_HonoursTheBatchLimit()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());

        var pending = await this._store.ListUnclassifiedAsync(limit: 1, maxAttempts: 3);

        Assert.Single(pending);
    }

    [Fact]
    public async Task ListUnclassifiedAsync_SkipsFindingsThatHaveExhaustedTheirAttempts()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var first = (await this._store.ListUnclassifiedAsync(10, 3)).First();

        await this._store.RecordClassificationAttemptAsync(first.Id);
        await this._store.RecordClassificationAttemptAsync(first.Id);
        await this._store.RecordClassificationAttemptAsync(first.Id);

        var pending = await this._store.ListUnclassifiedAsync(limit: 10, maxAttempts: 3);

        // A finding the model simply cannot place must stop costing a call on every sweep.
        Assert.DoesNotContain(pending, finding => finding.Id == first.Id);
        Assert.Equal(1, await this._store.CountUnclassifiedAsync(maxAttempts: 3));
    }

    [Fact]
    public async Task ApplyClassificationAsync_StoresTagsLevelQualifierAndConfidenceAndClearsTheBacklogEntry()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var pending = (await this._store.ListUnclassifiedAsync(10, 3)).First();

        await this._store.ApplyClassificationAsync(pending.Id, Classification("logic-error", "security"));

        var stored = await this._dbContext.CodeInsightFindings.SingleAsync(finding => finding.Id == pending.Id);
        Assert.Equal(CodeInsightFindingLevel.Member, stored.Level);
        Assert.Equal(CodeInsightFindingQualifier.Missing, stored.Qualifier);
        Assert.Equal(0.8, stored.ClassificationConfidence);
        Assert.NotNull(stored.ClassifiedAt);
        Assert.Equal(1, stored.ClassificationAttempts);

        var tags = await this._dbContext.CodeInsightFindingTags
            .Where(tag => tag.CodeInsightFindingId == pending.Id)
            .ToListAsync();
        Assert.Equal(2, tags.Count);
        Assert.All(tags, tag => Assert.True(tag.IsCore));
        Assert.All(tags, tag => Assert.Equal("test-classifier", tag.ClassifierVersion));
        Assert.All(tags, tag => Assert.Equal(CodeInsightCoreTaxonomy.Version, tag.TaxonomyVersion));

        Assert.DoesNotContain(
            await this._store.ListUnclassifiedAsync(10, 3),
            finding => finding.Id == pending.Id);
    }

    [Fact]
    public async Task ApplyClassificationAsync_ReplacesRatherThanAddsSoNoTypeIsCountedTwice()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var pending = (await this._store.ListUnclassifiedAsync(10, 3)).First();

        await this._store.ApplyClassificationAsync(pending.Id, Classification("logic-error", "security"));
        await this._store.ApplyClassificationAsync(pending.Id, Classification("logic-error"));

        var tags = await this._dbContext.CodeInsightFindingTags
            .Where(tag => tag.CodeInsightFindingId == pending.Id)
            .ToListAsync();

        // A re-classification leaves one set of tags, not two overlapping ones, every count it feeds would
        // otherwise be inflated with nothing to catch it.
        Assert.Single(tags);
        Assert.Equal("logic-error", tags[0].CoreSlug);
    }

    [Fact]
    public async Task ApplyClassificationAsync_DeduplicatesARepeatedTypeWithinOneVerdict()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var pending = (await this._store.ListUnclassifiedAsync(10, 3)).First();

        await this._store.ApplyClassificationAsync(
            pending.Id,
            Classification("logic-error", "logic-error", "LOGIC-ERROR"));

        Assert.Single(
            await this._dbContext.CodeInsightFindingTags
                .Where(tag => tag.CodeInsightFindingId == pending.Id)
                .ToListAsync());
    }

    [Fact]
    public async Task ApplyClassificationAsync_ForAnUnknownFinding_IsANoOp()
    {
        await this._store.ApplyClassificationAsync(Guid.CreateVersion7(), Classification("logic-error"));

        Assert.Empty(await this._dbContext.CodeInsightFindingTags.ToListAsync());
    }

    [Fact]
    public async Task RecordClassificationAttemptAsync_CountsUpWithoutMarkingTheFindingClassified()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var pending = (await this._store.ListUnclassifiedAsync(10, 3)).First();

        await this._store.RecordClassificationAttemptAsync(pending.Id);

        var stored = await this._dbContext.CodeInsightFindings.SingleAsync(finding => finding.Id == pending.Id);
        Assert.Equal(1, stored.ClassificationAttempts);
        Assert.Null(stored.ClassifiedAt);
        Assert.Contains(await this._store.ListUnclassifiedAsync(10, 3), finding => finding.Id == pending.Id);
    }

    [Fact]
    public async Task RecordDispositionAsync_StoresTheOutcomeWithItsSourceSignals()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).First();

        var decided = await this._store.RecordDispositionAsync(finding.Id, Disposition());

        Assert.True(decided);
        var stored = await this._store.GetDispositionAsync(finding.Id);
        Assert.NotNull(stored);
        Assert.Equal(CodeInsightDisposition.Addressed, stored.Disposition);
        // The signals travel with the verdict, so a disagreement can be settled from what it was derived
        // from rather than by re-judging a thread that has since moved on.
        Assert.Equal(ThreadResolutionIntent.ClaimsFix, stored.SourceIntent);
        Assert.Equal(ThreadAnchorCodeChange.Changed, stored.SourceCodeChange);
    }

    [Fact]
    public async Task RecordDispositionAsync_StoresARejectionReasonAndReadsItBack()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).First();

        await this._store.RecordDispositionAsync(
            finding.Id,
            Disposition(CodeInsightDisposition.Dismissed, CodeInsightRejectionReason.OutOfScope));

        var stored = await this._store.GetDispositionAsync(finding.Id);
        Assert.Equal(CodeInsightRejectionReason.OutOfScope, stored!.RejectionReason);
    }

    [Fact]
    public async Task RecordDispositionAsync_WithNoReason_StoresNoneRatherThanADefault()
    {
        // A finding that was fixed was never rejected, so there is no reason to record. Storing the first enum
        // value instead would put "the reviewer was wrong" on an outcome that says the opposite.
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).First();

        await this._store.RecordDispositionAsync(finding.Id, Disposition());

        var stored = await this._store.GetDispositionAsync(finding.Id);
        Assert.Null(stored!.RejectionReason);
    }

    [Fact]
    public async Task RecordDispositionAsync_LeavesAnAlreadyDecidedOutcomeUntouched()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).First();
        await this._store.RecordDispositionAsync(finding.Id, Disposition());

        var second = await this._store.RecordDispositionAsync(
            finding.Id,
            Disposition(CodeInsightDisposition.FalsePositive));

        Assert.False(second);
        // Exactly one outcome per finding, and it is the first one: a metric already computed from it must
        // not change underneath a report.
        Assert.Single(await this._dbContext.CodeInsightFindingDispositions.ToListAsync());
        Assert.Equal(
            CodeInsightDisposition.Addressed,
            (await this._store.GetDispositionAsync(finding.Id))!.Disposition);
    }

    [Fact]
    public async Task RecordDispositionAsync_ForAnUnknownFinding_RecordsNothing()
    {
        var decided = await this._store.RecordDispositionAsync(Guid.CreateVersion7(), Disposition());

        Assert.False(decided);
        Assert.Empty(await this._dbContext.CodeInsightFindingDispositions.ToListAsync());
    }

    [Fact]
    public async Task GetDispositionAsync_ReturnsNullWhileTheThreadIsStillOpen()
    {
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).First();

        Assert.Null(await this._store.GetDispositionAsync(finding.Id));
    }

    [Fact]
    public async Task RecordMissAsync_HarvestsAHumanThreadWithItsThreeJudgementsAndEncryptsTheDiscussion()
    {
        var key = NewKey();

        var recorded = await this._store.RecordMissAsync(key, Miss());

        Assert.True(recorded);
        var misses = await this._store.GetMissesForPullRequestAsync(key);
        var stored = Assert.Single(misses);
        Assert.Equal("thread-9", stored.ProviderThreadId);
        Assert.True(stored.CountsAsMiss);
        Assert.True(stored.IsSubstantive);
        Assert.True(stored.WasActedOn);
        Assert.True(stored.IsInScope);
        Assert.Equal("alice: this drops the retry count", stored.Discussion);

        var raw = await this._dbContext.CodeInsightMisses
            .Select(miss => miss.EncryptedDiscussion)
            .ToListAsync();
        Assert.All(raw, discussion => Assert.DoesNotContain("retry count", discussion, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordMissAsync_CreatesTheAggregateForAPullRequestThatHadNoFindings()
    {
        // A review that found nothing while a human found something is exactly the case recall exists for.
        var key = NewKey();

        await this._store.RecordMissAsync(key, Miss());

        Assert.Single(await this._dbContext.CodeInsightPullRequests.ToListAsync());
        Assert.Empty(await this._store.GetFindingsForPullRequestAsync(key));
        Assert.Single(await this._store.GetMissesForPullRequestAsync(key));
    }

    [Fact]
    public async Task RecordMissAsync_HarvestingTheSameThreadTwiceRecordsItOnce()
    {
        var key = NewKey();
        await this._store.RecordMissAsync(key, Miss());

        var second = await this._store.RecordMissAsync(key, Miss());

        Assert.False(second);
        // A crawl re-observes the same thread on every pass; harvesting it twice would double its
        // contribution to recall.
        Assert.Single(await this._store.GetMissesForPullRequestAsync(key));
    }

    [Fact]
    public async Task HasHarvestedThreadAsync_ReportsWhatHasAndHasNotBeenHarvested()
    {
        var key = NewKey();

        Assert.False(await this._store.HasHarvestedThreadAsync(key, "thread-9"));

        await this._store.RecordMissAsync(key, Miss());

        Assert.True(await this._store.HasHarvestedThreadAsync(key, "thread-9"));
        Assert.False(await this._store.HasHarvestedThreadAsync(key, "thread-other"));
    }

    [Fact]
    public async Task GetMissesForPullRequestAsync_ReturnsDisqualifiedThreadsToo()
    {
        // The threads that did not qualify are what make the cut-off inspectable.
        var key = NewKey();
        await this._store.RecordMissAsync(key, Miss());
        await this._store.RecordMissAsync(
            key,
            Miss("thread-10") with { IsInScope = false, CountsAsMiss = false });

        var misses = await this._store.GetMissesForPullRequestAsync(key);

        Assert.Equal(2, misses.Count);
        Assert.Single(misses, miss => miss.CountsAsMiss);
        Assert.Single(misses, miss => !miss.CountsAsMiss && !miss.IsInScope);
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesEverythingCollectedUnderTheAggregate()
    {
        // Every descendant, not just the findings: an orphaned tag, disposition, miss, or sealed measurement
        // would keep counting toward metrics for a pull request that no longer exists.
        var key = NewKey();
        await this._store.MaterialiseFindingsAsync(key, JobId, "rev-1", DateTimeOffset.UtcNow, NewFindings());
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).First();
        await this._store.ApplyClassificationAsync(finding.Id, Classification("logic-error"));
        await this._store.RecordDispositionAsync(finding.Id, Disposition());
        await this._store.RecordMissAsync(key, Miss());
        await this.SealAsync(key);

        // Age the aggregate directly. Every collection call legitimately moves the retention anchor forward
        // (collecting is activity), and the anchor never moves backwards, so the clock has to be wound here.
        var aggregate = await this._dbContext.CodeInsightPullRequests.SingleAsync();
        aggregate.LastActivityAt = DateTimeOffset.UtcNow.AddDays(-40);
        await this._dbContext.SaveChangesAsync();

        await this._store.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Empty(await this._dbContext.CodeInsightPullRequests.ToListAsync());
        Assert.Empty(await this._dbContext.CodeInsightFindings.ToListAsync());
        Assert.Empty(await this._dbContext.CodeInsightFindingTags.ToListAsync());
        Assert.Empty(await this._dbContext.CodeInsightFindingDispositions.ToListAsync());
        Assert.Empty(await this._dbContext.CodeInsightMisses.ToListAsync());
        Assert.Empty(await this._dbContext.CodeInsightPullRequestMetrics.ToListAsync());
    }

    /// <summary>
    ///     Writes a sealed measurement for the aggregate directly. The sealer itself is exercised elsewhere;
    ///     here the row only has to exist so the purge can be asked to remove it.
    /// </summary>
    private async Task SealAsync(CodeInsightPullRequestKey key)
    {
        var aggregateId = await this._dbContext.CodeInsightPullRequests
            .Where(candidate => candidate.ClientId == key.ClientId
                                && candidate.RepositoryId == key.RepositoryId
                                && candidate.PullRequestId == key.PullRequestId)
            .Select(candidate => candidate.Id)
            .SingleAsync();

        this._dbContext.CodeInsightPullRequestMetrics.Add(
            new CodeInsightPullRequestMetric
            {
                Id = Guid.CreateVersion7(),
                CodeInsightPullRequestId = aggregateId,
                ClientId = key.ClientId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                AddressedCount = 1,
                ResolvedCount = 1,
                Precision = 1d,
                CloseState = "Completed",
                SealedAt = DateTimeOffset.UtcNow,
                SealedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            });

        await this._dbContext.SaveChangesAsync();
    }

    private static CodeInsightMissRecord Miss(string providerThreadId = "thread-9")
    {
        return new CodeInsightMissRecord(
            providerThreadId,
            "src/Service.cs",
            42,
            "alice: this drops the retry count",
            true,
            true,
            true,
            true,
            0.8,
            "test-miss");
    }

    private static CodeInsightDispositionRecord Disposition(
        CodeInsightDisposition disposition = CodeInsightDisposition.Addressed,
        CodeInsightRejectionReason? rejectionReason = null)
    {
        return new CodeInsightDispositionRecord(
            disposition,
            ThreadResolutionIntent.ClaimsFix,
            ThreadAnchorCodeChange.Changed,
            null,
            null,
            rejectionReason);
    }

    private static CodeInsightClassification Classification(params string[] coreSlugs)
    {
        return new CodeInsightClassification(
            coreSlugs,
            [],
            CodeInsightFindingLevel.Member,
            CodeInsightFindingQualifier.Missing,
            0.8,
            "test-classifier");
    }

    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CodeInsightPullRequestKey NewKey()
    {
        return new CodeInsightPullRequestKey(Guid.NewGuid(), "repo-1", 100);
    }

    private static List<CodeInsightFindingSnapshot> NewFindings()
    {
        return
        [
            new CodeInsightFindingSnapshot(
                0,
                "src/Service.cs",
                42,
                CommentSeverity.Error,
                "Null dereference",
                "Baseline",
                null,
                null,
                false,
                ReviewCommentScopeRelation.OnChangedLine,
                ReviewCommentReadGrounding.Covered,
                "thread-1",
                "comment-1",
                "gpt-5.4-mini",
                "thrifty-reviewer",
                "Process",
                "Method"),
            new CodeInsightFindingSnapshot(
                1,
                null,
                null,
                CommentSeverity.Warning,
                "The change lacks tests",
                "PrWide",
                2,
                "security",
                false,
                null,
                null,
                null,
                null),
        ];
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.CodeInsightFindingStoreTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
