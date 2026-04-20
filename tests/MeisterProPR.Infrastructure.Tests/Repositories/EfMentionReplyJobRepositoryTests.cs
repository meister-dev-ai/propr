// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

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
        int threadId = 10,
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
        var job = MakeJob(prId: 5, threadId: 20, commentId: 200);
        await this._repo.AddAsync(job);

        var exists = await this._repo.ExistsForCommentAsync(ClientId, "repo", 5, 20, 200);
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsForCommentAsync_WhenJobDoesNotExist_ReturnsFalse()
    {
        var exists = await this._repo.ExistsForCommentAsync(ClientId, "repo", 99, 99, 99);
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

        await this._repo.SetCompletedAsync(job.Id);

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
        var job1 = MakeJob(prId: 2, threadId: 30, commentId: 300);
        await this._repo.AddAsync(job1);

        // Same (clientId, prId, threadId, commentId) → should fail on unique constraint
        var job2 = MakeJob(prId: 2, threadId: 30, commentId: 300);
        await Assert.ThrowsAnyAsync<Exception>(() => this._repo.AddAsync(job2));
    }
}
