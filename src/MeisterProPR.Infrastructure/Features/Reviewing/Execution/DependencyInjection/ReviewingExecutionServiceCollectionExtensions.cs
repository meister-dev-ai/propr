// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;

/// <summary>
///     Registers Reviewing execution boundaries.
/// </summary>
public static class ReviewingExecutionServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Reviewing execution adapters.
    /// </summary>
    public static IServiceCollection AddReviewingExecution(this IServiceCollection services)
    {
        services.AddScoped<IReviewJobExecutionStore>(sp =>
            new ReviewJobExecutionStoreAdapter(sp.GetRequiredService<IJobRepository>()));
        services.AddTransient<IReviewJobProcessor>(sp => sp.GetRequiredService<ReviewOrchestrationService>());

        return services;
    }
}
