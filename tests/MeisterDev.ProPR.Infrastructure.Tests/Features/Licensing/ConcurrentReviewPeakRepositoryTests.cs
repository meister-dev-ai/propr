// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     The record of the highest number of reviews seen executing at the same time on a UTC day. The day, the
///     count and the comparison all come from the database, so these run against PostgreSQL.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ConcurrentReviewPeakRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _jobIds = [];

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetAsync();
    }

    public Task DisposeAsync() => this.ResetAsync();

    // The count is installation-wide, so the assertion is made against what the table actually holds rather
    // than against the number this test seeded: the shared container carries whatever else is running.
    [Fact]
    public async Task AnObservation_RecordsTodaysExecutingCount()
    {
        await using var db = this.CreateContext();
        await this.SeedProcessingJobsAsync(2);

        await new ConcurrentReviewPeakRepository(db).ObserveAsync();

        var executing = await db.ReviewJobs.CountAsync(job => job.Status == JobStatus.Processing);
        var recorded = await this.ReadTodayAsync();

        Assert.NotNull(recorded);
        Assert.Equal(executing, recorded.PeakCount);
        Assert.True(recorded.PeakCount >= 2);
    }

    // What the day is read for is its highest point, so an observation made once the count has fallen must
    // leave the row where it is.
    [Fact]
    public async Task AnObservationBelowTheRecordedPeak_LeavesItWhereItWas()
    {
        await using var db = this.CreateContext();
        var sut = new ConcurrentReviewPeakRepository(db);
        await this.SeedProcessingJobsAsync(3);

        await sut.ObserveAsync();
        var peak = (await this.ReadTodayAsync())!.PeakCount;

        await this.CompleteOneSeededJobAsync();
        await sut.ObserveAsync();

        Assert.Equal(peak, (await this.ReadTodayAsync())!.PeakCount);
    }

    [Fact]
    public async Task ThePreviousDayRead_AnswersFromThatDaysRow()
    {
        await using var db = this.CreateContext();
        var yesterday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(-1);

        db.LicensingConcurrentReviewPeak.Add(
            new LicensingConcurrentReviewPeakRecord
            {
                PeakDate = yesterday,
                PeakCount = 7,
                ObservedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
        await db.SaveChangesAsync();

        await using var read = this.CreateContext();

        Assert.Equal(7, await new ConcurrentReviewPeakRepository(read).GetPreviousDayPeakAsync());
    }

    // A day with no executing review leaves no row, and the report says nothing rather than reporting a zero
    // that a reader could not tell apart from an installation that measures nothing.
    [Fact]
    public async Task ThePreviousDayRead_AnswersNothingWhenThatDayHasNoRow()
    {
        await using var db = this.CreateContext();

        Assert.Null(await new ConcurrentReviewPeakRepository(db).GetPreviousDayPeakAsync());
    }

    private async Task SeedProcessingJobsAsync(int count)
    {
        await using var seed = this.CreateContext();

        for (var index = 0; index < count; index++)
        {
            var job = LicensedResourceCountRepositoryTests.CreateJob(JobStatus.Processing);
            seed.ReviewJobs.Add(job);
            this._jobIds.Add(job.Id);
        }

        await seed.SaveChangesAsync();
    }

    private async Task CompleteOneSeededJobAsync()
    {
        await using var db = this.CreateContext();
        var jobId = this._jobIds[0];

        await db.ReviewJobs
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(job => job.SetProperty(row => row.Status, JobStatus.Completed));
    }

    private async Task<LicensingConcurrentReviewPeakRecord?> ReadTodayAsync()
    {
        await using var db = this.CreateContext();
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        return await db.LicensingConcurrentReviewPeak
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.PeakDate == today);
    }

    /// <summary>
    ///     Removes the jobs this test wrote and the peak rows for the two days it touches. The container is
    ///     shared across the collection, so anything else it holds is left alone.
    /// </summary>
    private async Task ResetAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var db = this.CreateContext();
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var yesterday = today.AddDays(-1);

        await db.ReviewJobs.Where(job => this._jobIds.Contains(job.Id)).ExecuteDeleteAsync();
        await db.LicensingConcurrentReviewPeak
            .Where(row => row.PeakDate == today || row.PeakDate == yesterday)
            .ExecuteDeleteAsync();

        this._jobIds.Clear();
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
