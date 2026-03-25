using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="ReviewFileResult" /> CRUD via <see cref="JobRepository" />
///     against a real PostgreSQL instance (Testcontainers).
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ReviewFileResultRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private JobRepository _repo = null!;

    public async Task DisposeAsync()
    {
        await this._dbContext.DisposeAsync();
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        this._repo = new JobRepository(this._dbContext);
    }

    private static ReviewJob MakeJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
    }

    // T036 — AddFileResultAsync persists the file result
    [Fact]
    public async Task AddFileResultAsync_PersistsResult()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var fileResult = new ReviewFileResult(job.Id, "src/Foo.cs");
        await this._repo.AddFileResultAsync(fileResult);

        var loaded = await this._repo.GetByIdWithFileResultsAsync(job.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.FileReviewResults);
        Assert.Equal("src/Foo.cs", loaded.FileReviewResults.First().FilePath);
        Assert.False(loaded.FileReviewResults.First().IsComplete);
    }

    // T036 — UpdateFileResultAsync updates IsComplete and PerFileSummary
    [Fact]
    public async Task UpdateFileResultAsync_UpdatesCompletionState()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var fileResult = new ReviewFileResult(job.Id, "src/Bar.cs");
        await this._repo.AddFileResultAsync(fileResult);

        fileResult.MarkCompleted("summary text", new List<ReviewComment>().AsReadOnly());
        await this._repo.UpdateFileResultAsync(fileResult);

        var loaded = await this._repo.GetByIdWithFileResultsAsync(job.Id);
        var storedResult = loaded!.FileReviewResults.First();
        Assert.True(storedResult.IsComplete);
        Assert.Equal("summary text", storedResult.PerFileSummary);
    }

    // T036 — GetByIdWithFileResultsAsync returns all file results for a job
    [Fact]
    public async Task GetByIdWithFileResultsAsync_ReturnsAllFileResults()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        await this._repo.AddFileResultAsync(new ReviewFileResult(job.Id, "src/A.cs"));
        await this._repo.AddFileResultAsync(new ReviewFileResult(job.Id, "src/B.cs"));

        var loaded = await this._repo.GetByIdWithFileResultsAsync(job.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.FileReviewResults.Count);
    }

    // T036 — UpdateFileResultAsync updates IsFailed and ErrorMessage
    [Fact]
    public async Task UpdateFileResultAsync_UpdatesFailureState()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var fileResult = new ReviewFileResult(job.Id, "src/C.cs");
        await this._repo.AddFileResultAsync(fileResult);

        fileResult.MarkFailed("AI timed out");
        await this._repo.UpdateFileResultAsync(fileResult);

        var loaded = await this._repo.GetByIdWithFileResultsAsync(job.Id);
        var storedResult = loaded!.FileReviewResults.First();
        Assert.True(storedResult.IsFailed);
        Assert.Equal("AI timed out", storedResult.ErrorMessage);
    }
}
