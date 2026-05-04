// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Diagnostics;

public sealed class ReviewingDiagnosticsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddReviewingModule_WithoutDatabaseConnectionString_ResolvesInMemoryDiagnosticsReader()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(withDatabaseConnectionString: false);

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
        public Task AddAsync(ReviewJob job, CancellationToken ct = default) => Task.CompletedTask;

        public ReviewJob? FindActiveJob(
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int iterationId) => null;

        public ReviewJob? FindCompletedJob(
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int iterationId) => null;

        public IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId) => [];

        public Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
            int limit,
            int offset,
            JobStatus? status,
            Guid? clientId = null,
            int? pullRequestId = null,
            CancellationToken ct = default)
            => Task.FromResult((0, (IReadOnlyList<ReviewJob>)[]));

        public ReviewJob? GetById(Guid id) => null;

        public IReadOnlyList<ReviewJob> GetPendingJobs() => [];

        public Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewJob>>([]);

        public Task<int> CountProcessingJobsAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(TimeSpan threshold, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewJob>>([]);

        public Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default) => Task.FromResult(false);

        public Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ReviewJob?>(null);

        public Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ReviewJob?> GetByIdWithProtocolsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ReviewJob?>(null);

        public Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ReviewJob?>(null);

        public Task SetCancelledAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ReviewJob>> GetActiveJobsForConfigAsync(
            string organizationUrl,
            string projectId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewJob>>([]);

        public Task<IReadOnlyList<ReviewJob>> GetByPrAsync(
            Guid clientId,
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int page,
            int pageSize,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewJob>>([]);

        public Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
            string organizationUrl,
            string projectId,
            string repositoryId,
            int pullRequestId,
            int iterationId,
            CancellationToken ct = default)
            => Task.FromResult<ReviewJob?>(null);

        public Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default, float? reviewTemperature = null)
            => Task.CompletedTask;

        public Task UpdatePrContextAsync(
            Guid id,
            string? prTitle,
            string? prRepositoryName,
            string? prSourceBranch,
            string? prTargetBranch,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
