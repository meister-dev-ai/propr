// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.StopReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.Runtime;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Intake.DependencyInjection;

/// <summary>Registers the Reviewing.Intake vertical slice.</summary>
public static class ReviewingIntakeServiceCollectionExtensions
{
    /// <summary>Registers handlers, persistence, and runtime adapters for Reviewing.Intake.</summary>
    public static IServiceCollection AddReviewingIntake(this IServiceCollection services)
    {
        services.AddScoped<IReviewJobIntakeStore>(sp =>
        {
            var jobRepository = sp.GetRequiredService<IJobRepository>();
            return jobRepository is InMemoryReviewJobRepository inMemoryRepository
                ? new OfflineReviewJobIntakeStore(inMemoryRepository)
                : new EfReviewJobIntakeStore(sp.GetRequiredService<MeisterProPRDbContext>());
        });

        services.AddSingleton<InMemoryBlockedPullRequestStore>();
        services.AddScoped<IBlockedPullRequestStore>(sp =>
        {
            var jobRepository = sp.GetRequiredService<IJobRepository>();
            return jobRepository is InMemoryReviewJobRepository
                ? sp.GetRequiredService<InMemoryBlockedPullRequestStore>()
                : new BlockedPullRequestStore(
                    sp.GetRequiredService<MeisterProPRDbContext>(),
                    sp.GetRequiredService<ILogger<BlockedPullRequestStore>>());
        });

        services.AddScoped<IReviewExecutionQueue, ReviewExecutionQueue>();
        services.AddScoped<SubmitReviewJobHandler>();
        services.AddScoped<RestartReviewJobHandler>();
        services.AddScoped<StopReviewJobHandler>();
        services.AddScoped<GetReviewJobStatusHandler>();

        return services;
    }
}
