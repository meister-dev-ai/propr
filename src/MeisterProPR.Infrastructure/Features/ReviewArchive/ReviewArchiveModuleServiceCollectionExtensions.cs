// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.Features.ReviewArchive;

/// <summary>Registers the review-archive module for opt-in-retained raw pull-request data.</summary>
public static class ReviewArchiveModuleServiceCollectionExtensions
{
    /// <summary>Registers the review-archive store when database-backed runtime services are available.</summary>
    public static IServiceCollection AddReviewArchiveModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (!configuration.HasDatabaseConnectionString())
        {
            return services;
        }

        // Both stores take an optional IDbContextFactory<MeisterProPRDbContext> and run their
        // reads + writes on a fresh factory context so a best-effort archival failure can never leave
        // tracked entities behind that poison the shared request-scoped context. The factory is registered
        // by the infrastructure module under the same database-connection-string gate as this module, so it
        // is always available here and the container injects it into the stores automatically.
        services.AddScoped<IReviewArchiveStore, ReviewArchiveStore>();
        services.AddScoped<IReviewArchiveIngestionService, ReviewArchiveIngestionService>();
        services.AddScoped<IPostedCommentOriginStore, PostedCommentOriginStore>();

        return services;
    }
}
