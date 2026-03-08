using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Repositories;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

public class InMemoryJobRepositoryTests
{
    private static ReviewJob CreateJob(
        string clientKey = "client-a",
        string orgUrl = "https://dev.azure.com/org",
        string projectId = "proj",
        string repoId = "repo",
        int prId = 1,
        int iterationId = 1)
    {
        return new ReviewJob(Guid.NewGuid(), clientKey, orgUrl, projectId, repoId, prId, iterationId);
    }

    [Fact]
    public void Add_ThenGetById_ReturnsSameJob()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);

        var retrieved = repo.GetById(job.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(job.Id, retrieved.Id);
    }

    [Fact]
    public void FindActiveJob_ReturnsExistingCompletedJob()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);
        job.Status = JobStatus.Completed;

        var found = repo.FindActiveJob(job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId);
        Assert.NotNull(found);
    }

    [Fact]
    public void FindActiveJob_ReturnsExistingPendingJob()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);

        var found = repo.FindActiveJob(job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId);
        Assert.NotNull(found);
        Assert.Equal(job.Id, found.Id);
    }

    [Fact]
    public void FindActiveJob_ReturnsExistingProcessingJob()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);
        job.Status = JobStatus.Processing;

        var found = repo.FindActiveJob(job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId);
        Assert.NotNull(found);
    }

    [Fact]
    public void FindActiveJob_ReturnsNullForFailedJob()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);
        job.Status = JobStatus.Failed;

        var found = repo.FindActiveJob(job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId);
        Assert.Null(found);
    }

    [Fact]
    public void FindActiveJob_ReturnsNullWhenNoMatchingJob()
    {
        var repo = new InMemoryJobRepository();

        var found = repo.FindActiveJob("https://dev.azure.com/org", "proj", "repo", 999, 1);
        Assert.Null(found);
    }

    [Fact]
    public void GetAllForClient_OnlyReturnsJobsForClient()
    {
        var repo = new InMemoryJobRepository();
        repo.Add(CreateJob());
        repo.Add(CreateJob("client-b", prId: 2));

        var jobs = repo.GetAllForClient("client-a");
        Assert.Single(jobs);
        Assert.All(jobs, j => Assert.Equal("client-a", j.ClientKey));
    }

    [Fact]
    public void GetAllForClient_ReturnsNewestFirst()
    {
        var repo = new InMemoryJobRepository();
        var job1 = CreateJob();
        var job2 = CreateJob(prId: 2);

        // Add slight delay to ensure different SubmittedAt times
        repo.Add(job1);
        Thread.Sleep(10);
        var job3 = CreateJob(prId: 3);
        repo.Add(job2);
        repo.Add(job3);

        var jobs = repo.GetAllForClient("client-a");

        Assert.Equal(3, jobs.Count);
        // Verify descending order (newest first)
        for (var i = 0; i < jobs.Count - 1; i++)
        {
            Assert.True(jobs[i].SubmittedAt >= jobs[i + 1].SubmittedAt);
        }
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        var repo = new InMemoryJobRepository();
        Assert.Null(repo.GetById(Guid.NewGuid()));
    }

    [Fact]
    public void GetPendingJobs_ReturnsOldestFirst()
    {
        var repo = new InMemoryJobRepository();
        var job1 = CreateJob(prId: 1);
        repo.Add(job1);
        Thread.Sleep(10);
        var job2 = CreateJob(prId: 2);
        repo.Add(job2);

        var pendingJobs = repo.GetPendingJobs();
        Assert.Equal(2, pendingJobs.Count);
        Assert.True(pendingJobs[0].SubmittedAt <= pendingJobs[1].SubmittedAt);
    }

    [Fact]
    public void GetPendingJobs_ReturnsOnlyPendingJobs()
    {
        var repo = new InMemoryJobRepository();
        var pending = CreateJob(prId: 1);
        var processing = CreateJob(prId: 2);
        var completed = CreateJob(prId: 3);
        repo.Add(pending);
        repo.Add(processing);
        repo.Add(completed);
        processing.Status = JobStatus.Processing;
        completed.Status = JobStatus.Completed;

        var pendingJobs = repo.GetPendingJobs();
        Assert.Single(pendingJobs);
        Assert.Equal(pending.Id, pendingJobs[0].Id);
    }

    [Fact]
    public void SetFailed_PopulatesErrorMessageAndSetsFailed()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);

        repo.SetFailed(job.Id, "Something went wrong");

        var retrieved = repo.GetById(job.Id)!;
        Assert.Equal(JobStatus.Failed, retrieved.Status);
        Assert.Equal("Something went wrong", retrieved.ErrorMessage);
        Assert.NotNull(retrieved.CompletedAt);
    }

    [Fact]
    public void SetResult_PopulatesResultAndSetsCompleted()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);
        var result = new ReviewResult("summary", new List<ReviewComment>().AsReadOnly());

        repo.SetResult(job.Id, result);

        var retrieved = repo.GetById(job.Id)!;
        Assert.Equal(JobStatus.Completed, retrieved.Status);
        Assert.NotNull(retrieved.Result);
        Assert.Equal("summary", retrieved.Result.Summary);
        Assert.NotNull(retrieved.CompletedAt);
    }

    [Fact]
    public void TryTransition_UnknownId_ReturnsFalse()
    {
        var repo = new InMemoryJobRepository();
        Assert.False(repo.TryTransition(Guid.NewGuid(), JobStatus.Pending, JobStatus.Processing));
    }

    [Fact]
    public void TryTransition_ValidTransition_ReturnsTrueAndUpdatesStatus()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);

        var result = repo.TryTransition(job.Id, JobStatus.Pending, JobStatus.Processing);

        Assert.True(result);
        Assert.Equal(JobStatus.Processing, repo.GetById(job.Id)!.Status);
    }

    [Fact]
    public void TryTransition_WrongFromState_ReturnsFalse()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob();
        repo.Add(job);

        var result = repo.TryTransition(job.Id, JobStatus.Processing, JobStatus.Completed);

        Assert.False(result);
        Assert.Equal(JobStatus.Pending, repo.GetById(job.Id)!.Status);
    }
}