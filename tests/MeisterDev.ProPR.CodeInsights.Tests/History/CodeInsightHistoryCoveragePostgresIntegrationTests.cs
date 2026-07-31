// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.History;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.CodeInsights.Tests.History;

/// <summary>
///     Coverage counts the findings a review persisted by summing a <c>jsonb</c> array in the database, which the
///     in-memory provider cannot execute at all: it has no json functions, so the unit tests take a different
///     branch and prove nothing about the one production uses. This runs that branch against real PostgreSQL.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class CodeInsightHistoryCoveragePostgresIntegrationTests(PostgresContainerFixture fixture)
    : IAsyncLifetime
{
    private static readonly DateTimeOffset InWindow = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private MeisterProPRDbContext _dbContext = null!;
    private CodeInsightHistoryReader _reader = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        var now = DateTimeOffset.UtcNow;
        this._dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId,
                Slug = "cov-" + this._tenantId.ToString("N"),
                DisplayName = "Coverage Test Tenant",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = this._tenantId,
                DisplayName = "Coverage Test Client",
                IsActive = true,
                CreatedAt = now,
            });
        await this._dbContext.SaveChangesAsync();

        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        this._reader = new CodeInsightHistoryReader(this._dbContext, gate);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task Sums_the_findings_a_review_persisted_over_real_postgres()
    {
        await this.SeedJobAsync("repo-1", pullRequestId: 41, findingsPerFile: [3, 2]);
        await this.SeedJobAsync("repo-1", pullRequestId: 42, findingsPerFile: [1]);
        // A file result with no comments at all: excluded and failed results carry none, and they must not throw
        // or count.
        await this.SeedJobAsync("repo-1", pullRequestId: 43, findingsPerFile: []);

        var coverage = await this._reader.GetCoverageAsync(
            new CodeInsightHistoryCoverageQuery([this._clientId], new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));

        var row = Assert.Single(coverage.Rows);
        Assert.Equal(3, row.ReviewJobs);
        Assert.Equal(6, row.ProducedFindings);
        Assert.Equal(0, row.CollectedFindings);
        Assert.Equal(6, coverage.ProducedFindings);
    }

    [Fact]
    public async Task Counts_a_revision_reviewed_twice_once_taking_the_larger_review()
    {
        // Collection keys a finding by its revision and its position in it, so the second review's findings land on
        // the first review's rows and only the positions it did not reach are added. Summing per job would report
        // this repository as half collected forever.
        await this.SeedJobAsync("repo-1", pullRequestId: 61, findingsPerFile: [4]);
        await this.SeedJobAsync("repo-1", pullRequestId: 61, findingsPerFile: [2]);

        var coverage = await this._reader.GetCoverageAsync(
            new CodeInsightHistoryCoverageQuery([this._clientId], new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));

        var row = Assert.Single(coverage.Rows);
        // Both jobs ran, and they reviewed one revision between them.
        Assert.Equal(2, row.ReviewJobs);
        Assert.Equal(4, row.ProducedFindings);
    }

    [Fact]
    public async Task Counts_two_iterations_of_one_pull_request_separately()
    {
        // The guard on the rule above: a second iteration is a different revision with its own findings, and
        // collapsing it into the first would under-report what the reviews actually produced.
        await this.SeedJobAsync("repo-1", pullRequestId: 62, findingsPerFile: [4], iterationId: 1);
        await this.SeedJobAsync("repo-1", pullRequestId: 62, findingsPerFile: [2], iterationId: 2);

        var coverage = await this._reader.GetCoverageAsync(
            new CodeInsightHistoryCoverageQuery([this._clientId], new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));

        var row = Assert.Single(coverage.Rows);
        Assert.Equal(6, row.ProducedFindings);
    }

    private async Task SeedJobAsync(
        string repositoryId,
        int pullRequestId,
        IReadOnlyList<int> findingsPerFile,
        int iterationId = 1)
    {
        var jobId = Guid.NewGuid();
        this._dbContext.ReviewJobs.Add(
            new ReviewJob(
                jobId,
                this._clientId,
                "https://dev.azure.com/org",
                "project",
                repositoryId,
                pullRequestId,
                iterationId)
            {
                SubmittedAt = InWindow,
                Status = JobStatus.Completed,
                CompletedAt = InWindow,
            });

        for (var index = 0; index < Math.Max(findingsPerFile.Count, 1); index++)
        {
            var result = new ReviewFileResult(jobId, $"src/File{index}.cs");
            if (index < findingsPerFile.Count)
            {
                result.MarkCompleted(
                    "summary",
                    Enumerable
                        .Range(0, findingsPerFile[index])
                        .Select(ordinal => new ReviewComment(
                            $"src/File{index}.cs",
                            ordinal + 1,
                            CommentSeverity.Warning,
                            $"finding {ordinal}"))
                        .ToList());
            }

            this._dbContext.ReviewFileResults.Add(result);
        }

        await this._dbContext.SaveChangesAsync();
    }
}
