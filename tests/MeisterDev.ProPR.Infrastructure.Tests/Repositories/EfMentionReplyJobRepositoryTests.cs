// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="EfMentionReplyJobRepository" /> against a real PostgreSQL instance.
///     Uses a shared <see cref="PostgresContainerFixture" /> to avoid container-per-test instability.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class EfMentionReplyJobRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    // Deterministic client ID so FK constraint is satisfied across test runs.
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private MeisterProPRDbContext _dbContext = null!;
    private EfMentionReplyJobRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Seed the client for FK constraint — use ON CONFLICT DO NOTHING pattern.
        if (!await this._dbContext.Clients.AnyAsync(c => c.Id == ClientId))
        {
            this._dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = ClientId,
                    TenantId = TenantCatalog.SystemTenantId,
                    DisplayName = "Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._dbContext.SaveChangesAsync();
        }

        // Wipe mention reply jobs between tests.
        await this._dbContext.MentionReplyJobs.ExecuteDeleteAsync();
        this._repo = new EfMentionReplyJobRepository(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        // Clean up mention_reply_jobs so the shared client row can be deleted by other test classes.
        await this._dbContext.MentionReplyJobs.ExecuteDeleteAsync();

        // The thread pass seeded for the owner-rule test holds the same client down.
        await this._dbContext.ThreadPassJobs.Where(p => p.PullRequestId == 777).ExecuteDeleteAsync();

        // The second client exists only for the cross-client race test and would otherwise outlive it.
        var secondClientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await this._dbContext.Clients.Where(c => c.Id == secondClientId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    private static MentionReplyJob MakeJob(
        Guid? clientId = null,
        int prId = 1,
        string threadId = "10",
        int commentId = 100,
        string mentionText = "what does this do?")
    {
        return new MentionReplyJob(
            Guid.NewGuid(),
            clientId ?? ClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            prId,
            threadId,
            commentId,
            mentionText);
    }


    [Fact]
    public async Task AddAsync_ThenGetPendingAsync_ReturnsJob()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var pending = await this._repo.GetPendingAsync();
        Assert.Single(pending);
        Assert.Equal(job.Id, pending[0].Id);
        Assert.Equal(MentionJobStatus.Pending, pending[0].Status);
    }


    [Fact]
    public async Task ExistsForCommentAsync_WhenJobExists_ReturnsTrue()
    {
        var job = MakeJob(prId: 5, threadId: "20", commentId: 200);
        job.SetMentionedReviewer(MakeReviewer());
        await this._repo.AddAsync(job);

        var exists = await this._repo.ExistsForCommentAsync(
            "repo",
            5,
            "20",
            200,
            MakeReviewer().AddressedKey);
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsForCommentAsync_WhenADifferentBotWasAddressed_ReturnsFalse()
    {
        // A mention of another reviewer account on the same comment is a different question, and the client
        // that answers for that account must not be told the work is already taken.
        var job = MakeJob(prId: 6, threadId: "21", commentId: 210);
        job.SetMentionedReviewer(MakeReviewer());
        await this._repo.AddAsync(job);

        var exists = await this._repo.ExistsForCommentAsync(
            "repo",
            6,
            "21",
            210,
            MakeReviewer("other-bot-external-id").AddressedKey);
        Assert.False(exists);
    }

    [Fact]
    public async Task TryAddAsync_WhenAnotherClientAlreadyTookTheComment_ReturnsFalseWithoutThrowing()
    {
        // Two clients cover the repository and neither can see the other's configuration. The database is
        // what decides, and losing is an ordinary outcome rather than a fault.
        var first = MakeJob(prId: 8, threadId: "24", commentId: 240);
        first.SetMentionedReviewer(MakeReviewer());
        Assert.True(await this._repo.TryAddAsync(first));

        var second = MakeJob(clientId: await this.SeedSecondClientAsync(), prId: 8, threadId: "24", commentId: 240);
        second.SetMentionedReviewer(MakeReviewer());

        Assert.False(await this._repo.TryAddAsync(second));

        var stored = await this._dbContext.MentionReplyJobs
            .AsNoTracking()
            .Where(j => j.PullRequestId == 8 && j.ThreadId == "24" && j.CommentId == 240)
            .ToListAsync();
        Assert.Single(stored);
        Assert.Equal(first.ClientId, stored[0].ClientId);
    }

    /// <summary>A real thread pass, so a two-owner insert is rejected by the owner rule and not by a key.</summary>
    private async Task<Guid> SeedThreadPassAsync()
    {
        var pass = new ThreadPassJob(
            Guid.NewGuid(),
            ClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            777,
            1,
            "1",
            $"1|{Guid.NewGuid()}");

        this._dbContext.ThreadPassJobs.Add(pass);
        await this._dbContext.SaveChangesAsync();
        return pass.Id;
    }

    /// <summary>
    ///     A second client, so one client losing the comment to another is exercised against the real foreign
    ///     key. The two attempts are sequential on purpose: what is under test is that the unique index
    ///     rejects the second write and the repository reports it as an ordinary false, which is the same
    ///     path a genuinely concurrent pair would take once the database has serialized them.
    /// </summary>
    private async Task<Guid> SeedSecondClientAsync()
    {
        var secondClientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        if (!await this._dbContext.Clients.AnyAsync(c => c.Id == secondClientId))
        {
            this._dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = secondClientId,
                    TenantId = TenantCatalog.SystemTenantId,
                    DisplayName = "Second Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._dbContext.SaveChangesAsync();
        }

        return secondClientId;
    }

    private static ReviewerIdentity MakeReviewer(string externalUserId = "review-bot-external-id")
    {
        return new ReviewerIdentity(
            new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org"),
            externalUserId,
            "review-bot",
            "Review Bot",
            true);
    }

    [Fact]
    public async Task ExistsForCommentAsync_WhenJobDoesNotExist_ReturnsFalse()
    {
        var exists = await this._repo.ExistsForCommentAsync(
            "repo",
            99,
            "99",
            99,
            MakeReviewer().AddressedKey);
        Assert.False(exists);
    }


    [Fact]
    public async Task TryTransitionAsync_ValidTransition_ReturnsTrueAndUpdatesStatus()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var transitioned = await this._repo.TryTransitionAsync(
            job.Id,
            MentionJobStatus.Pending,
            MentionJobStatus.Processing);
        Assert.True(transitioned);

        var pending = await this._repo.GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task TryTransitionAsync_WrongCurrentStatus_ReturnsFalse()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        // Job is Pending, try to transition from Processing → Completed (invalid)
        var transitioned = await this._repo.TryTransitionAsync(
            job.Id,
            MentionJobStatus.Processing,
            MentionJobStatus.Completed);
        Assert.False(transitioned);

        // Status should still be Pending
        var pending = await this._repo.GetPendingAsync();
        Assert.Single(pending);
    }


    [Fact]
    public async Task SetCompletedAsync_UpdatesStatusAndCompletedAt()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);

        await this._repo.SetCompletedAsync(job.Id, "4711");

        var pending = await this._repo.GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task SetFailedAsync_UpdatesStatusAndErrorMessage()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);

        await this._repo.SetFailedAsync(job.Id, "AI endpoint timeout");

        var pending = await this._repo.GetPendingAsync();
        Assert.Empty(pending);
    }


    [Fact]
    public async Task ResetStuckProcessingAsync_ResetsProcessingJobsToPending()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);

        // Simulate crash recovery: reset stuck processing jobs
        await this._repo.ResetStuckProcessingAsync();

        var pending = await this._repo.GetPendingAsync();
        Assert.Single(pending);
        Assert.Equal(MentionJobStatus.Pending, pending[0].Status);
    }


    [Fact]
    public async Task AddAsync_DuplicateComment_ThrowsOnUniqueViolation()
    {
        var job1 = MakeJob(prId: 2, threadId: "30", commentId: 300);
        await this._repo.AddAsync(job1);

        // Same (clientId, prId, threadId, commentId) → should fail on unique constraint
        var job2 = MakeJob(prId: 2, threadId: "30", commentId: 300);
        await Assert.ThrowsAnyAsync<Exception>(() => this._repo.AddAsync(job2));
    }

    [Fact]
    public async Task GetPostedRepliesAsync_ReturnsTheAnswerWithEveryCoordinateItsProvenanceNeeds()
    {
        // The point of persisting the comment id: a lost provenance row is rebuildable from the job alone.
        var job = MakeJob(prId: 7, threadId: "70", commentId: 700);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);
        await this._repo.SetCompletedAsync(job.Id, "answer-comment-5");

        var posted = await this._repo.GetPostedRepliesAsync(DateTimeOffset.UtcNow.AddHours(-1), 100);

        var reply = Assert.Single(posted);
        Assert.Equal(job.Id, reply.JobId);
        Assert.Equal(ClientId, reply.ClientId);
        Assert.Equal("repo", reply.RepositoryId);
        Assert.Equal(7, reply.PullRequestId);
        Assert.Equal("70", reply.ProviderThreadId);
        Assert.Equal("answer-comment-5", reply.ProviderCommentId);
        Assert.NotEqual(default, reply.PostedAt);
    }

    [Fact]
    public async Task GetPostedRepliesAsync_JobThatPostedNothingIdentifiable_IsNotReturned()
    {
        // An adapter that reported no comment id leaves nothing to attribute the comment by, so the job is not
        // a recovery candidate. It is a job whose answer can never carry provenance either way.
        var job = MakeJob(prId: 8, threadId: "80", commentId: 800);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);
        await this._repo.SetCompletedAsync(job.Id, null);

        var posted = await this._repo.GetPostedRepliesAsync(DateTimeOffset.UtcNow.AddHours(-1), 100);

        Assert.Empty(posted);
    }

    [Fact]
    public async Task GetPostedRepliesAsync_AnswerOlderThanTheWindow_IsNotReturned()
    {
        var job = MakeJob(prId: 9, threadId: "90", commentId: 900);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);
        await this._repo.SetCompletedAsync(job.Id, "answer-comment-9");

        var posted = await this._repo.GetPostedRepliesAsync(DateTimeOffset.UtcNow.AddHours(1), 100);

        Assert.Empty(posted);
    }

    [Fact]
    public async Task GetPostedRepliesAsync_UnfinishedJob_IsNotReturned()
    {
        var job = MakeJob(prId: 11, threadId: "110", commentId: 1100);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);

        var posted = await this._repo.GetPostedRepliesAsync(DateTimeOffset.UtcNow.AddHours(-1), 100);

        Assert.Empty(posted);
    }

    [Fact]
    public async Task GetPostedRepliesAsync_MoreAnswersThanTheCap_ReturnsTheMostRecentOnes()
    {
        var older = MakeJob(prId: 12, threadId: "120", commentId: 1200);
        await this._repo.AddAsync(older);
        await this._repo.TryTransitionAsync(older.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);
        await this._repo.SetCompletedAsync(older.Id, "answer-comment-older");

        var newer = MakeJob(prId: 13, threadId: "130", commentId: 1300);
        await this._repo.AddAsync(newer);
        await this._repo.TryTransitionAsync(newer.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);
        await this._repo.SetCompletedAsync(newer.Id, "answer-comment-newer");

        // Space the two apart explicitly rather than trusting two clock reads a few milliseconds apart to
        // order the way the assertion needs.
        await this._dbContext.MentionReplyJobs
            .Where(j => j.Id == older.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow.AddMinutes(-10)));

        var posted = await this._repo.GetPostedRepliesAsync(DateTimeOffset.UtcNow.AddHours(-1), 1);

        var reply = Assert.Single(posted);
        Assert.Equal(newer.Id, reply.JobId);
    }

    [Fact]
    public async Task SetCompletedAsync_AfterAnotherContextRecordedTheSpend_LeavesTheTotalsStanding()
    {
        // The repository writes through the request-scoped context while the protocol recorder accumulates
        // spend through a short-lived one of its own. The row is already tracked here by the time the answer
        // is completed, so a completion that carried its stale copy of the totals back would zero out spend
        // that had just been recorded.
        var job = MakeJob(prId: 21, threadId: "210", commentId: 2100);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);
        await this._repo.SetExecutionContextAsync(job.Id, 4, Guid.NewGuid(), "gpt-4o");

        // Stands in for EfProtocolRecorder's own context closing the trace record.
        await using (var recorderContext = new MeisterProPRDbContext(
                         new DbContextOptionsBuilder<MeisterProPRDbContext>()
                             .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                             .Options))
        {
            var tracked = await recorderContext.MentionReplyJobs.FirstAsync(j => j.Id == job.Id);
            tracked.AccumulateSpend(1_200, 300, 0.42m);
            await recorderContext.SaveChangesAsync();
        }

        await this._repo.SetCompletedAsync(job.Id, "answer-comment-21");

        await using var verify = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options);
        var stored = await verify.MentionReplyJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);

        Assert.Equal(MentionJobStatus.Completed, stored.Status);
        Assert.Equal("answer-comment-21", stored.PostedReplyCommentId);
        Assert.Equal(1_200, stored.TotalInputTokens);
        Assert.Equal(300, stored.TotalOutputTokens);
        Assert.Equal(0.42m, stored.TotalEstimatedCostUsd);
        Assert.Equal(4, stored.IterationId);
    }

    [Fact]
    public async Task TraceRecordOwnership_AcceptsExactlyOneOwnerAndRejectsNoneOrTwo()
    {
        // Widening the owner rule from a pair to a count is only safe if it still admits exactly one. A row
        // with two owners would have its tokens counted against two units of work.
        var job = MakeJob(prId: 23, threadId: "230", commentId: 2300);
        await this._repo.AddAsync(job);

        var accepted = await Record.ExceptionAsync(() => this._dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO review_job_protocols (id, mention_reply_job_id, attempt_number, started_at, cache_observability)
            VALUES ({0}, {1}, 1, now(), 0);
            """,
            Guid.NewGuid(),
            job.Id));
        Assert.Null(accepted);

        var ownerless = await Record.ExceptionAsync(() => this._dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO review_job_protocols (id, attempt_number, started_at, cache_observability)
            VALUES ({0}, 1, now(), 0);
            """,
            Guid.NewGuid()));

        // Pinned to the constraint, as the two-owner case below is. Accepting any PostgresException would
        // let an unrelated rule reject the insert and still read as proof of the owner-count one.
        var ownerlessPostgres = Assert.IsType<PostgresException>(ownerless?.InnerException ?? ownerless);
        Assert.Equal("ck_review_job_protocols_single_owner", ownerlessPostgres.ConstraintName);

        // Both owners must be rows that really exist, or the foreign keys reject the insert first and the
        // test passes without the owner-count rule ever being consulted.
        var threadPassId = await this.SeedThreadPassAsync();
        var twoOwners = await Record.ExceptionAsync(() => this._dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO review_job_protocols (id, mention_reply_job_id, thread_pass_job_id, attempt_number, started_at, cache_observability)
            VALUES ({0}, {1}, {2}, 1, now(), 0);
            """,
            Guid.NewGuid(),
            job.Id,
            threadPassId));

        var twoOwnersPostgres = Assert.IsType<PostgresException>(twoOwners?.InnerException ?? twoOwners);
        Assert.Equal("ck_review_job_protocols_single_owner", twoOwnersPostgres.ConstraintName);

        // The rows this test wrote must go before the mention job they hang off can be deleted.
        await this._dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM review_job_protocols WHERE mention_reply_job_id = {0};",
            job.Id);
    }

    [Fact]
    public async Task SetBudgetHeldAsync_RecordsTheCapThatStoppedTheAnswer()
    {
        var job = MakeJob(prId: 22, threadId: "220", commentId: 2200);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing);

        await this._repo.SetBudgetHeldAsync(job.Id, 4, BudgetScopeKind.ClientMonthly, BudgetCapKind.Hard, 10m, 11.5m);

        await using var verify = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options);
        var stored = await verify.MentionReplyJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);

        Assert.Equal(MentionJobStatus.BudgetHeld, stored.Status);
        Assert.Equal(BudgetScopeKind.ClientMonthly, stored.BudgetBlockScope);
        Assert.Equal(BudgetCapKind.Hard, stored.BudgetBlockCapKind);
        Assert.Equal(10m, stored.BudgetBlockThresholdUsd);
        Assert.Equal(11.5m, stored.BudgetBlockSpentUsd);
        Assert.NotNull(stored.CompletedAt);
    }
}
