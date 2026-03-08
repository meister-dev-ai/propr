using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Entities;

public class ReviewJobTests
{
    private static ReviewJob CreateJob(
        Guid? id = null,
        string clientKey = "test-client",
        string orgUrl = "https://dev.azure.com/myorg",
        string projectId = "proj-1",
        string repoId = "repo-1",
        int prId = 1,
        int iterationId = 1)
    {
        return new ReviewJob(id ?? Guid.NewGuid(), clientKey, orgUrl, projectId, repoId, prId, iterationId);
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
    public void Constructor_SetsAllFields()
    {
        var id = Guid.NewGuid();
        var job = new ReviewJob(id, "key", "https://dev.azure.com/org", "proj", "repo", 42, 3);

        Assert.Equal(id, job.Id);
        Assert.Equal("key", job.ClientKey);
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
    public void Constructor_ThrowsOnEmptyClientKey()
    {
        Assert.Throws<ArgumentException>(() => CreateJob(clientKey: ""));
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
}