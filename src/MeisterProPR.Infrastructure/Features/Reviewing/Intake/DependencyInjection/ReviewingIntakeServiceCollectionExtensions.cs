// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Intake.DependencyInjection;

/// <summary>Registers the Reviewing.Intake vertical slice.</summary>
public static class ReviewingIntakeServiceCollectionExtensions
{
    /// <summary>Registers handlers, persistence, and runtime adapters for Reviewing.Intake.</summary>
    public static IServiceCollection AddReviewingIntake(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IReviewJobIntakeStore, EfReviewJobIntakeStore>();
        services.AddScoped<IReviewExecutionQueue, ReviewExecutionQueue>();
        services.AddScoped<SubmitReviewJobHandler>();
        services.AddScoped<GetReviewJobStatusHandler>();

        return services;
    }
}
