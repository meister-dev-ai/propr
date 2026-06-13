// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;

/// <summary>
///     Registers Reviewing diagnostics boundaries and read models.
/// </summary>
public static class ReviewingDiagnosticsServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Reviewing diagnostics services.
    /// </summary>
    public static IServiceCollection AddReviewingDiagnostics(this IServiceCollection services)
    {
        services.AddScoped<IReviewDiagnosticsReader>(sp =>
        {
            var jobRepository = sp.GetRequiredService<IJobRepository>();
            var dbContextFactory = sp.GetService<IDbContextFactory<MeisterProPRDbContext>>();
            return jobRepository is InMemoryReviewJobRepository inMemoryRepository
                ? new InMemoryReviewDiagnosticsReader(inMemoryRepository)
                : new EfReviewDiagnosticsReader(jobRepository, dbContextFactory);
        });
        services.AddScoped<GetReviewJobProtocolHandler>();
        services.AddScoped<GetFileDiffHandler>();

        return services;
    }
}
