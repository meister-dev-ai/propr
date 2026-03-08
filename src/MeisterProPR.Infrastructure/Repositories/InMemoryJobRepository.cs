using System.Collections.Concurrent;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Repositories;

public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<Guid, ReviewJob> _jobs = new();

    public ReviewJob? FindActiveJob(string organizationUrl, string projectId, string repositoryId, int pullRequestId, int iterationId)
    {
        return this._jobs.Values.FirstOrDefault(j =>
            j.OrganizationUrl == organizationUrl &&
            j.ProjectId == projectId &&
            j.RepositoryId == repositoryId &&
            j.PullRequestId == pullRequestId &&
            j.IterationId == iterationId &&
            j.Status != JobStatus.Failed);
    }

    public void Add(ReviewJob job)
    {
        this._jobs[job.Id] = job;
    }

    public ReviewJob? GetById(Guid id)
    {
        return this._jobs.GetValueOrDefault(id);
    }

    public IReadOnlyList<ReviewJob> GetAllForClient(string clientKey)
    {
        return this._jobs.Values
            .Where(j => j.ClientKey == clientKey)
            .OrderByDescending(j => j.SubmittedAt)
            .ToList();
    }

    public bool TryTransition(Guid id, JobStatus from, JobStatus to)
    {
        if (!this._jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.Status != from)
            {
                return false;
            }

            job.Status = to;
            return true;
        }
    }

    public void SetResult(Guid id, ReviewResult result)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            lock (job)
            {
                job.Result = result;
                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public void SetFailed(Guid id, string errorMessage)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            lock (job)
            {
                job.ErrorMessage = errorMessage;
                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public IReadOnlyList<ReviewJob> GetPendingJobs()
    {
        return this._jobs.Values
            .Where(j => j.Status == JobStatus.Pending)
            .OrderBy(j => j.SubmittedAt)
            .ToList();
    }
}