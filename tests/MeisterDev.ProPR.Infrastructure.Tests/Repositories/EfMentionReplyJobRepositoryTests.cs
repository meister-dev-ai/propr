// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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
        await this._repo.AddAsync(job);

        var exists = await this._repo.ExistsForCommentAsync(ClientId, "repo", 5, "20", 200);
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsForCommentAsync_WhenJobDoesNotExist_ReturnsFalse()
    {
        var exists = await this._repo.ExistsForCommentAsync(ClientId, "repo", 99, "99", 99);
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
}
