// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using MeisterProPR.Infrastructure.Services;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Features.ReviewArchive;

/// <summary>
///     End-to-end retention coverage against a real PostgreSQL instance (Testcontainers / pgvector),
///     with only the event producer faked. Exercises the full opt-in data path over the real schema
///     (applied via migrations): provenance stamping, thread + diff ingestion, encryption-at-rest on the
///     real columns, server-side connection resolution on read (clientId + repositoryId + pullRequestId,
///     no connectionId), the most-recently-active tie-break across connections, and the worker's purge
///     sequence leaving unrelated review and memory rows intact.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ReviewArchiveRetentionPostgresIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private ISecretProtectionCodec _codec = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private ReviewArchiveIngestionService _ingestion = null!;
    private PostedCommentOriginStore _originStore = null!;
    private ReviewArchiveStore _store = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Isolate from any rows other tests in the shared collection may have left behind.
        await this._dbContext.RetainedPullRequests.ExecuteDeleteAsync();
        await this._dbContext.PostedCommentOrigins.ExecuteDeleteAsync();
        await this._dbContext.ThreadMemoryRecords.ExecuteDeleteAsync();
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();

        this._codec = CreateCodec();
        this._store = new ReviewArchiveStore(this._dbContext, this._codec);
        this._ingestion = new ReviewArchiveIngestionService(this._store);
        this._originStore = new PostedCommentOriginStore(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task RetentionDataPath_IngestsReadsResolvesAndPurges_OverRealPostgres()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string repositoryId = "repo-retention";
        const long pullRequestId = 4321;

        const string humanBody = "Please rename this method for clarity.";
        const string aiBody = "Renamed the method and tightened the null check as suggested.";
        const string unifiedDiff = "@@ -1,3 +1,4 @@\n-var total = Sum(items);\n+var total = Sum(items) ?? 0;\n+Log(total);";

        var publishedHuman = DateTimeOffset.UtcNow.AddMinutes(-12);
        var publishedAi = DateTimeOffset.UtcNow.AddMinutes(-10);
        var lastActivity = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Step 1 — the crawl/publication side records the provenance of the AI comment, then the
        // stamping pass resolves which review job produced each retained comment of the pull request.
        // Azure DevOps comment ids are thread-local, so a single publish that opens two threads can stamp
        // the same comment id ("1") on each. Record both: the thread id disambiguates them, and both must
        // insert cleanly (no 23505 on the unique constraint) and resolve to their respective jobs. A third
        // origin models the non-ADO providers (GitHub/GitLab/Forgejo): a globally-unique comment id ("555")
        // recorded against a review/discussion id ("review-7") that the crawler will not echo back.
        var originatingJobId = Guid.NewGuid();
        var secondThreadJobId = Guid.NewGuid();
        var uniqueCommentJobId = Guid.NewGuid();
        await this._originStore.RecordAsync(
        [
            new PostedCommentOriginEntry(
                clientId,
                repositoryId,
                pullRequestId,
                "thread-9",
                "1",
                originatingJobId,
                publishedAi),
            new PostedCommentOriginEntry(
                clientId,
                repositoryId,
                pullRequestId,
                "thread-10",
                "1",
                secondThreadJobId,
                publishedAi),
            new PostedCommentOriginEntry(
                clientId,
                repositoryId,
                pullRequestId,
                "review-7",
                "555",
                uniqueCommentJobId,
                publishedAi),
        ]);

        var provenance = await this._originStore.GetJobIdsForPullRequestAsync(clientId, repositoryId, pullRequestId);
        Assert.Equal(3, provenance.Count);

        // ADO thread-local collision: comment id "1" appears under two threads, so the thread id breaks the tie.
        var resolvedJobId = await this._originStore.GetJobIdForCommentAsync(clientId, repositoryId, pullRequestId, "thread-9", "1");
        Assert.Equal(originatingJobId, resolvedJobId);
        Assert.Equal(secondThreadJobId, await this._originStore.GetJobIdForCommentAsync(clientId, repositoryId, pullRequestId, "thread-10", "1"));

        // Non-ADO unique comment id: resolves on the comment id alone even though the crawled thread id
        // ("thread-abc") differs from the recorded one, and likewise when the crawler reports no thread id.
        Assert.Equal(uniqueCommentJobId, await this._originStore.GetJobIdForCommentAsync(clientId, repositoryId, pullRequestId, "thread-abc", "555"));
        Assert.Equal(uniqueCommentJobId, await this._originStore.GetJobIdForCommentAsync(clientId, repositoryId, pullRequestId, null, "555"));

        // Step 2 — ingest a thread carrying a human comment and an AI comment; the AI comment carries the
        // looked-up originating job id, exactly as the crawl-side stamp produces it.
        await this._ingestion.HandleThreadUpdatedAsync(
            new ThreadUpdatedEvent(
                clientId,
                connectionId,
                repositoryId,
                pullRequestId,
                "thread-9",
                "src/Domain/Cart.cs",
                64,
                "active",
                lastActivity,
                [
                    new ThreadUpdatedComment("comment-human", "alice@example.com", false, publishedHuman, humanBody),
                    new ThreadUpdatedComment("comment-ai", "propr-bot", true, publishedAi, aiBody, resolvedJobId),
                ]));

        // Step 3 — ingest a per-increment diff for a file under the same pull request.
        await this._ingestion.HandleReviewIncrementDiffsAsync(
            new ReviewIncrementCompletedEvent(
                clientId,
                connectionId,
                repositoryId,
                pullRequestId,
                "rev-1",
                "active",
                lastActivity,
                [new ReviewIncrementFileDiff("src/Domain/Cart.cs", "Modified", false, unifiedDiff)]));

        // Step 4 — read back through the server-side-resolution path (no connection id supplied).
        var threads = await this._store.GetThreadsForPullRequestAsync(clientId, repositoryId, pullRequestId);
        var thread = Assert.Single(threads);
        Assert.Equal("thread-9", thread.ThreadId);
        Assert.Equal("src/Domain/Cart.cs", thread.FilePath);
        Assert.Equal(64, thread.Line);
        Assert.Equal("active", thread.Status);
        Assert.Equal(2, thread.Comments.Count);

        var human = thread.Comments.Single(comment => comment.CommentId == "comment-human");
        Assert.False(human.IsAiAuthored);
        Assert.Equal(humanBody, human.Text);
        Assert.Null(human.OriginatingJobId);

        var ai = thread.Comments.Single(comment => comment.CommentId == "comment-ai");
        Assert.True(ai.IsAiAuthored);
        Assert.Equal(aiBody, ai.Text);
        Assert.Equal(originatingJobId, ai.OriginatingJobId);

        var fileDiff = await this._store.GetFileDiffAsync(clientId, repositoryId, pullRequestId, null, "src/Domain/Cart.cs");
        Assert.NotNull(fileDiff);
        Assert.Equal("rev-1", fileDiff!.RevisionKey);
        Assert.Equal(unifiedDiff, fileDiff.UnifiedDiff);

        // Step 4 (encryption-at-rest) — query the raw columns directly and confirm the stored text is
        // ciphertext, never the plaintext bodies.
        // EF Core's scalar SqlQueryRaw<string> projects the single result column named "Value".
        var storedCommentTexts = await this._dbContext.Database
            .SqlQueryRaw<string>(
                "SELECT encrypted_text AS \"Value\" FROM retained_thread_comments WHERE comment_id IN ({0}, {1})",
                "comment-human",
                "comment-ai")
            .ToListAsync();
        Assert.Equal(2, storedCommentTexts.Count);
        Assert.All(storedCommentTexts, text => Assert.True(this._codec.IsProtected(text)));
        Assert.DoesNotContain(storedCommentTexts, text => text.Contains(humanBody, StringComparison.Ordinal));
        Assert.DoesNotContain(storedCommentTexts, text => text.Contains(aiBody, StringComparison.Ordinal));

        var storedDiffText = await this._dbContext.Database
            .SqlQueryRaw<string>(
                "SELECT encrypted_unified_diff AS \"Value\" FROM retained_file_diffs WHERE file_path = {0}",
                "src/Domain/Cart.cs")
            .SingleAsync();
        Assert.True(this._codec.IsProtected(storedDiffText));
        Assert.DoesNotContain("var total", storedDiffText, StringComparison.Ordinal);

        // Step 5 — a second connection retains the same (client, repo, pr) with a later activity time.
        // The read with no connection id must resolve to the most recently active connection's data.
        var secondConnectionId = Guid.NewGuid();
        const string newerDiff = "@@ -1,4 +1,4 @@\n-Log(total);\n+Log(total, level: Info);";
        await this._ingestion.HandleReviewIncrementDiffsAsync(
            new ReviewIncrementCompletedEvent(
                clientId,
                secondConnectionId,
                repositoryId,
                pullRequestId,
                "rev-1",
                "active",
                DateTimeOffset.UtcNow,
                [new ReviewIncrementFileDiff("src/Domain/Cart.cs", "Modified", false, newerDiff)]));

        var resolvedDiff = await this._store.GetFileDiffAsync(clientId, repositoryId, pullRequestId, null, "src/Domain/Cart.cs");
        Assert.NotNull(resolvedDiff);
        Assert.Equal(newerDiff, resolvedDiff!.UnifiedDiff);

        // Step 6 — seed unrelated memory and review rows in the same context, then run the worker's purge
        // sequence for the connections that retained this pull request. The retained archive and its
        // provenance must be removed while the unrelated rows survive.
        var memoryRecord = SeedThreadMemoryRecord(clientId, repositoryId, (int)pullRequestId);
        await this.SeedClientAsync(clientId);
        this._dbContext.ThreadMemoryRecords.Add(memoryRecord);

        var reviewJob = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", repositoryId, 1, 1);
        var reviewFileResult = new ReviewFileResult(reviewJob.Id, "src/Domain/Cart.cs");
        reviewFileResult.MarkCompleted(
            "Per-file summary",
            [new ReviewComment("src/Domain/Cart.cs", 64, CommentSeverity.Warning, "Consider a clearer name.")]);
        reviewJob.FileReviewResults.Add(reviewFileResult);
        this._dbContext.ReviewJobs.Add(reviewJob);

        await this._dbContext.SaveChangesAsync();

        // Mirror the worker's full-purge decision: purge the retained archive for each connection that held
        // this pull request. The archive purge removes each pull request's provenance in the same
        // transaction, so no separate provenance pass is issued — the assertions below prove the archive
        // purge alone cleared the posted-comment origins.
        var purgedAggregates = 0;
        foreach (var purgeConnectionId in new[] { connectionId, secondConnectionId })
        {
            purgedAggregates += await this._store.PurgeForConnectionAsync(purgeConnectionId);
        }

        // Both connections' retained pull-request aggregates were removed.
        Assert.Equal(2, purgedAggregates);

        // The retained threads, comments, diffs, and provenance for the pull request are gone.
        Assert.Empty(await this._store.GetThreadsForPullRequestAsync(clientId, repositoryId, pullRequestId));
        Assert.Null(await this._store.GetFileDiffAsync(clientId, repositoryId, pullRequestId, null, "src/Domain/Cart.cs"));
        Assert.Equal(0, await this._dbContext.RetainedPullRequests.CountAsync());
        Assert.Equal(0, await this._dbContext.RetainedThreads.CountAsync());
        Assert.Equal(0, await this._dbContext.RetainedThreadComments.CountAsync());
        Assert.Equal(0, await this._dbContext.RetainedFileDiffs.CountAsync());
        Assert.Empty(await this._originStore.GetJobIdsForPullRequestAsync(clientId, repositoryId, pullRequestId));

        // The unrelated memory and review rows are untouched.
        var persistedMemory = await this._dbContext.ThreadMemoryRecords.AsNoTracking().SingleAsync();
        Assert.Equal(memoryRecord.Id, persistedMemory.Id);
        Assert.Equal("The issue was resolved by renaming the method.", persistedMemory.ResolutionSummary);

        var persistedJob = await this._dbContext.ReviewJobs.AsNoTracking().SingleAsync();
        Assert.Equal(reviewJob.Id, persistedJob.Id);
        var persistedFileResult = await this._dbContext.ReviewFileResults.AsNoTracking().SingleAsync();
        Assert.Equal(reviewFileResult.Id, persistedFileResult.Id);
        Assert.Equal("src/Domain/Cart.cs", persistedFileResult.FilePath);
    }

    private static ThreadMemoryRecord SeedThreadMemoryRecord(Guid clientId, string repositoryId, int pullRequestId)
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-15);
        return new ThreadMemoryRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ThreadId = 999,
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            FilePath = "src/Domain/Cart.cs",
            ChangeExcerpt = "- old\n+ new",
            CommentHistoryDigest = "alice: rename this\npropr-bot: done",
            ResolutionSummary = "The issue was resolved by renaming the method.",
            EmbeddingVector = Embedding(0.1f),
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            MemorySource = MemorySource.ThreadResolved,
        };
    }

    private async Task SeedClientAsync(Guid clientId)
    {
        if (await this._dbContext.Clients.AnyAsync(client => client.Id == clientId))
        {
            return;
        }

        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = $"Retention Test Client {clientId:N}",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
    }

    private static float[] Embedding(float seed)
    {
        var vector = new float[1536];
        vector[0] = seed;
        return vector;
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterProPR.ReviewArchiveRetentionPostgresIntegrationTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
