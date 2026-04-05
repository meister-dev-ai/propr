// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Entities;

public class ReviewJobTests
{
    private static ReviewJob CreateJob(
        Guid? id = null,
        Guid? clientId = null,
        string orgUrl = "https://dev.azure.com/myorg",
        string projectId = "proj-1",
        string repoId = "repo-1",
        int prId = 1,
        int iterationId = 1)
    {
        return new ReviewJob(id ?? Guid.NewGuid(), clientId ?? Guid.NewGuid(), orgUrl, projectId, repoId, prId, iterationId);
    }

    [Fact]
    public void Constructor_CompletedAtIsNull()
    {
        var job = CreateJob();
        Assert.Null(job.CompletedAt);
    }

    [Fact]
    public void Constructor_DefaultsStatusToPending()
    {
        var job = CreateJob();
        Assert.Equal(JobStatus.Pending, job.Status);
    }

    [Fact]
    public void Constructor_IdIsNonEmptyGuid()
    {
        var job = CreateJob();
        Assert.NotEqual(Guid.Empty, job.Id);
    }

    [Fact]
    public void Constructor_ProcessingStartedAt_IsNullByDefault()
    {
        var job = CreateJob();
        Assert.Null(job.ProcessingStartedAt);
    }

    [Fact]
    public void Constructor_SetsAllFields()
    {
        var id = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var job = new ReviewJob(id, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 3);

        Assert.Equal(id, job.Id);
        Assert.Equal(clientId, job.ClientId);
        Assert.Equal("https://dev.azure.com/org", job.OrganizationUrl);
        Assert.Equal("proj", job.ProjectId);
        Assert.Equal("repo", job.RepositoryId);
        Assert.Equal(42, job.PullRequestId);
        Assert.Equal(3, job.IterationId);
    }

    [Fact]
    public void Constructor_SubmittedAtIsSet()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var job = CreateJob();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.InRange(job.SubmittedAt, before, after);
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => CreateJob(Guid.Empty));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyOrganizationUrl()
    {
        Assert.Throws<ArgumentException>(() => CreateJob(orgUrl: ""));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyProjectId()
    {
        Assert.Throws<ArgumentException>(() => CreateJob(projectId: ""));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyRepositoryId()
    {
        Assert.Throws<ArgumentException>(() => CreateJob(repoId: ""));
    }

    [Fact]
    public void Constructor_ThrowsOnZeroIterationId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateJob(iterationId: 0));
    }

    [Fact]
    public void Constructor_ThrowsOnZeroPullRequestId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateJob(prId: 0));
    }

    [Fact]
    public void Constructor_WithClientId_SetsClientId()
    {
        var clientId = Guid.NewGuid();
        var job = CreateJob(clientId: clientId);
        Assert.Equal(clientId, job.ClientId);
    }

    [Fact]
    public void ResultAndErrorMessage_AreNullByDefault()
    {
        var job = CreateJob();
        Assert.Null(job.Result);
        Assert.Null(job.ErrorMessage);
    }

    [Fact]
    public void Status_CanBeChanged()
    {
        var job = CreateJob();
        job.Status = JobStatus.Processing;
        Assert.Equal(JobStatus.Processing, job.Status);
    }

    /// <summary>
    /// Backward compat invariant: TotalInputTokensAggregated must ALWAYS equal the
    /// sum of all TotalInputTokens across all TokenBreakdownEntry items, regardless
    /// of whether tokens are accumulated via AccumulateTokens or AccumulateTierTokens.
    /// </summary>
    [Fact]
    public void AccumulateTierTokens_MaintainsBackwardCompatInvariant()
    {
        var job = CreateJob();

        // Accumulate tokens for multiple tiers
        job.AccumulateTierTokens(AiConnectionModelCategory.LowEffort, "gpt-4o-mini", 100, 50);
        job.AccumulateTierTokens(AiConnectionModelCategory.HighEffort, "gpt-4o", 500, 150);
        job.AccumulateTierTokens(AiConnectionModelCategory.Default, "(default)", 200, 100);

        // Verify tokenBreakdown sum equals aggregates
        var breakdownInputTotal = job.TokenBreakdown.Sum(e => e.TotalInputTokens);
        var breakdownOutputTotal = job.TokenBreakdown.Sum(e => e.TotalOutputTokens);

        Assert.Equal(job.TotalInputTokensAggregated, breakdownInputTotal);
        Assert.Equal(job.TotalOutputTokensAggregated, breakdownOutputTotal);
        Assert.Equal(100 + 500 + 200, job.TotalInputTokensAggregated);
        Assert.Equal(50 + 150 + 100, job.TotalOutputTokensAggregated);
    }

    [Fact]
    public void AccumulateTierTokens_MergesExistingEntry()
    {
        var job = CreateJob();

        job.AccumulateTierTokens(AiConnectionModelCategory.HighEffort, "gpt-4o", 100, 50);
        job.AccumulateTierTokens(AiConnectionModelCategory.HighEffort, "gpt-4o", 50, 30);

        // Should have only one entry, with merged tokens
        Assert.Single(job.TokenBreakdown);
        var entry = job.TokenBreakdown[0];
        Assert.Equal(150, entry.TotalInputTokens);
        Assert.Equal(80, entry.TotalOutputTokens);
        Assert.Equal(150, job.TotalInputTokensAggregated);
        Assert.Equal(80, job.TotalOutputTokensAggregated);
    }

    [Fact]
    public void AccumulateTierTokens_TracksMultipleTiers()
    {
        var job = CreateJob();

        job.AccumulateTierTokens(AiConnectionModelCategory.LowEffort, "gpt-4o-mini", 100, 50);
        job.AccumulateTierTokens(AiConnectionModelCategory.MediumEffort, "gpt-4o-mini", 200, 100);
        job.AccumulateTierTokens(AiConnectionModelCategory.HighEffort, "gpt-4o", 300, 150);

        Assert.Equal(3, job.TokenBreakdown.Count);
        Assert.Equal(600, job.TotalInputTokensAggregated);
        Assert.Equal(300, job.TotalOutputTokensAggregated);
    }

    [Fact]
    public void AccumulateTierTokens_DifferentiatesByModel()
    {
        var job = CreateJob();

        job.AccumulateTierTokens(AiConnectionModelCategory.HighEffort, "gpt-4o", 100, 50);
        job.AccumulateTierTokens(AiConnectionModelCategory.HighEffort, "gpt-4-turbo", 200, 100);

        // Should have 2 entries (same tier, different models)
        Assert.Equal(2, job.TokenBreakdown.Count);
        Assert.Equal(300, job.TotalInputTokensAggregated);
        Assert.Equal(150, job.TotalOutputTokensAggregated);
    }
}

