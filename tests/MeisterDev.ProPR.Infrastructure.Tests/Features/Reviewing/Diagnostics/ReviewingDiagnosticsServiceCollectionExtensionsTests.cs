// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Admission.Models;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Diagnostics;

public sealed class ReviewingDiagnosticsServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddReviewingDiagnostics_PrReviewView_UsesTheRegisteredInMemoryDbContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<IJobRepository, FakeJobRepository>();
        services.AddReviewingDiagnostics();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://provider.example", "project", "repository", 373, 1);
        job.Protocols.Add(
            new ReviewJobProtocol
            {
                Id = Guid.NewGuid(), JobId = job.Id, AttemptNumber = 1, StartedAt = DateTimeOffset.UtcNow, TotalInputTokens = 123,
            });
        db.ReviewJobs.Add(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var data = await scope.ServiceProvider.GetRequiredService<IPrReviewViewReader>()
            .ReadAsync(job.ClientId, job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, 1, 20);

        var summary = Assert.Single(data.Jobs);
        Assert.Equal(job.Id, summary.JobId);
        Assert.Equal(123, summary.TotalInputTokens);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task AddReviewingModule_PrReviewView_UsesTheOfflineJobRepository()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(false);
        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://provider.example", "project", "repository", 373, 1);
        await repository.AddAsync(job);

        var data = await scope.ServiceProvider.GetRequiredService<IPrReviewViewReader>()
            .ReadAsync(job.ClientId, job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, 1, 20);

        Assert.Equal(job.Id, Assert.Single(data.Jobs).JobId);
        Assert.Empty(data.ThreadPasses);
        Assert.Null(data.Watermark);
    }

    [Fact]
    public void AddReviewingModule_WithoutDatabaseConnectionString_ResolvesInMemoryDiagnosticsReader()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(false);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnosticsReader = scope.ServiceProvider.GetRequiredService<IReviewDiagnosticsReader>();

        Assert.IsType<InMemoryReviewDiagnosticsReader>(diagnosticsReader);
    }

    [Fact]
    public void AddReviewingDiagnostics_WithEfJobRepository_ResolvesEfDiagnosticsReader()
    {
        var services = new ServiceCollection();
        services.AddScoped<IJobRepository, FakeJobRepository>();
        services.AddReviewingDiagnostics();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnosticsReader = scope.ServiceProvider.GetRequiredService<IReviewDiagnosticsReader>();

        Assert.IsType<EfReviewDiagnosticsReader>(diagnosticsReader);
    }

    [Fact]
    public async Task AddReviewingDiagnostics_ReadsAProtocolThroughTheJobRepositoryTheHostRegistered()
    {
        // The diagnostics reader is exercised, not the repository underneath it: resolving that repository
        // here and calling it would pass whatever the registration hands the reader.
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 1);
        var repository = new FakeJobRepository { Job = job };
        var services = new ServiceCollection();
        services.AddScoped<IJobRepository>(_ => repository);
        services.AddReviewingDiagnostics();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var protocol = await scope.ServiceProvider.GetRequiredService<IReviewDiagnosticsReader>()
            .GetJobProtocolAsync(job.Id);

        Assert.NotNull(protocol);
        Assert.Equal(job.ClientId, protocol.ClientId);
        Assert.Equal(job.Id, repository.RequestedJobId);
    }

    private static IConfiguration CreateConfiguration(bool withDatabaseConnectionString)
    {
        var values = new Dictionary<string, string?>
        {
            ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
            ["ADO_STUB_PR"] = "true",
            ["MEISTER_JWT_SECRET"] = "test-reviewing-diagnostics-jwt-secret-32!",
            ["DB_CONNECTION_STRING"] = withDatabaseConnectionString
                ? "Host=localhost;Database=meister;Username=test;Password=test"
                : null,
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class FakeJobRepository : IJobRepository
    {
        public Task<IReadOnlyList<ReviewJob>> FindActiveByProviderCoordinatesAsync(
            Guid clientId, ScmProvider provider, string scope, string project,
            string repository, int pullRequestId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewJob>>([]);

        public Task AddAsync(ReviewJob job, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<TryAddReviewJobResult> TryAddIfNoActiveDuplicateAsync(ReviewJob job, CancellationToken ct = default)
        {
            return Task.FromResult(new TryAddReviewJobResult(true, null, 0));
        }

        public ReviewJob? FindActiveJob(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int iterationId)
        {
            return null;
        }

        public ReviewJob? FindCompletedJob(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int iterationId)
        {
            return null;
        }

        public ReviewJob? FindFailedJob(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int iterationId)
        {
            return null;
        }

        public IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId)
        {
            return [];
        }

        public Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
            int limit,
            int offset,
            JobStatus? status,
            Guid? clientId = null,
            int? pullRequestId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult((0, (IReadOnlyList<ReviewJob>)[]));
        }

        public Task<(int total, IReadOnlyList<JobListPageItemDto> items)> GetJobListPageAsync(
            int limit,
            int offset,
            JobStatus? status,
            Guid? clientId = null,
            int? pullRequestId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult((0, (IReadOnlyList<JobListPageItemDto>)[]));
        }

        public Task<(int total, IReadOnlyList<JobListPageItemDto> items)> GetJobListPageAsync(
            int limit,
            int offset,
            JobStatus? status,
            IEnumerable<Guid>? clientIds,
            int? pullRequestId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult((0, (IReadOnlyList<JobListPageItemDto>)[]));
        }

        public Task<(int total, IReadOnlyList<PullRequestHistoryGroupDto> items)> GetPullRequestHistoryPageAsync(
            int limit,
            int offset,
            JobStatus? status,
            Guid? clientId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult((0, (IReadOnlyList<PullRequestHistoryGroupDto>)[]));
        }

        public Task<(int total, IReadOnlyList<PullRequestHistoryGroupDto> items)> GetPullRequestHistoryPageAsync(
            int limit,
            int offset,
            JobStatus? status,
            IEnumerable<Guid>? clientIds,
            CancellationToken ct = default)
        {
            return Task.FromResult((0, (IReadOnlyList<PullRequestHistoryGroupDto>)[]));
        }

        public ReviewJob? GetById(Guid id)
        {
            return null;
        }

        public IReadOnlyList<ReviewJob> GetPendingJobs()
        {
            return [];
        }

        public Task<IReadOnlyList<ReviewJob>> GetClaimCandidatesAsync(int limit, DateTimeOffset? submittedAfter = null, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ReviewJob>>([]);
        }

        public Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ReviewJob>>([]);
        }

        public Task<int> CountProcessingJobsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(0);
        }

        public Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateInScopeChangedFileCountAsync(Guid id, int count, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> CountReviewedFilesAsync(Guid jobId, CancellationToken ct = default)
        {
            return Task.FromResult(0);
        }

        public Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default)
        {
            return Task.FromResult<ReviewJob?>(null);
        }

        public Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>The job this double answers protocol reads with.</summary>
        public ReviewJob? Job { get; set; }

        /// <summary>The job id the last protocol read asked for.</summary>
        public Guid? RequestedJobId { get; private set; }

        public Task<ReviewJob?> GetByIdWithProtocolsAsync(Guid id, CancellationToken ct = default)
        {
            this.RequestedJobId = id;
            return Task.FromResult(this.Job is { } job && job.Id == id ? job : null);
        }

        public Task<ReviewJob?> GetByIdWithProtocolsForOverviewAsync(Guid id, CancellationToken ct = default)
        {
            return Task.FromResult<ReviewJob?>(null);
        }

        public Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default)
        {
            return Task.FromResult<ReviewJob?>(null);
        }

        public Task SetCancelledAsync(Guid id, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetSupersededAsync(Guid id, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<SupersededReviewJobState?> TrySupersedeAsync(
            Guid id,
            JobStatus expectedStatus,
            CancellationToken ct = default)
        {
            return Task.FromResult<SupersededReviewJobState?>(null);
        }

        public Task<bool> TryRestoreSupersededAsync(
            Guid id,
            SupersededReviewJobState state,
            CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public Task SetStoppedAsync(Guid id, string reason, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetStoppedAsync(Guid id, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetBudgetExceededAsync(
            Guid id,
            MeisterDev.ProPR.Domain.Enums.BudgetScopeKind? scope,
            MeisterDev.ProPR.Domain.Enums.BudgetCapKind capKind,
            decimal? thresholdUsd,
            decimal? spentUsd,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetBudgetHeldAsync(
            Guid id,
            MeisterDev.ProPR.Domain.Enums.BudgetScopeKind scope,
            MeisterDev.ProPR.Domain.Enums.BudgetCapKind capKind,
            decimal thresholdUsd,
            decimal spentUsd,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetAdmissionHeldAsync(Guid id, DateTimeOffset heldUntil, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> SetAdmissionRefusedAsync(Guid id, string reason, string? policyFingerprint, CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> SetQueuedAdmissionRefusedAsync(Guid id, string reason, CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public Task<RefusedReviewAdmission?> GetLatestRefusedAdmissionAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            CancellationToken ct = default)
        {
            return Task.FromResult<RefusedReviewAdmission?>(null);
        }

        public Task<int> ReleaseDueAdmissionHoldsAsync(DateTimeOffset now, CancellationToken ct = default)
        {
            return Task.FromResult(0);
        }

        /// <summary>What this repository reports for the rolling admission window. Empty until a test sets it.</summary>
        public ReviewSubmissionWindow SubmissionWindow { get; set; } = ReviewSubmissionWindow.Empty;

        public Task<ReviewSubmissionWindow> GetSubmissionWindowAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            DateTimeOffset since,
            Guid excludeJobId,
            CancellationToken ct = default)
        {
            return Task.FromResult(this.SubmissionWindow);
        }

        public Task<IReadOnlyList<ReviewJob>> GetActiveJobsForConfigAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ReviewJob>>([]);
        }

        public Task<string?> FindRecordedRepositoryIdAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryName,
            int pullRequestId,
            CancellationToken ct = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<ReviewJob>> GetByPrAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ReviewJob>>([]);
        }

        public Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int iterationId,
            CancellationToken ct = default)
        {
            return Task.FromResult<ReviewJob?>(null);
        }

        public Task<ReviewJob?> GetCompletedJobWithFileResultsByStoredRevisionAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            string storedRevisionKey,
            CancellationToken ct = default)
        {
            return Task.FromResult<ReviewJob?>(null);
        }

        public Task<ReviewJob?> GetLatestReusableTerminalJobAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            Guid excludeJobId,
            string currentRevisionKey,
            CancellationToken ct = default)
        {
            return Task.FromResult<ReviewJob?>(null);
        }

        public Task<EngagedReviewRevision?> GetLatestEngagedRevisionAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            CancellationToken ct = default)
        {
            return Task.FromResult<EngagedReviewRevision?>(null);
        }

        public Task<ReviewJob?> GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            string storedRevisionKey,
            CancellationToken ct = default)
        {
            return Task.FromResult<ReviewJob?>(null);
        }

        public Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default, float? reviewTemperature = null)
        {
            return Task.CompletedTask;
        }

        public Task UpdatePrContextAsync(
            Guid id,
            string? prTitle,
            string? prRepositoryName,
            string? prSourceBranch,
            string? prTargetBranch,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdatePullRequestAuthorAsync(Guid id, PullRequestAuthor author, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
