// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.DependencyInjection;

/// <summary>
///     Registers provider-neutral local review workspace services.
/// </summary>
public static class ReviewWorkspaceServiceCollectionExtensions
{
    /// <summary>
    ///     Adds provider-neutral review workspace services and binds review workspace configuration.
    /// </summary>
    public static IServiceCollection AddReviewWorkspaceServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReviewWorkspaceOptions>()
            .Configure(options =>
            {
                if (!string.IsNullOrWhiteSpace(configuration["REVIEW_WORKSPACE_ROOT_PATH"]))
                {
                    options.RootPath = configuration["REVIEW_WORKSPACE_ROOT_PATH"]!;
                }

                if (int.TryParse(configuration["REVIEW_WORKSPACE_RETENTION_MINUTES"], out var retentionMinutes))
                {
                    options.RetentionMinutes = retentionMinutes;
                }

                if (int.TryParse(configuration["REVIEW_WORKSPACE_MAX_CACHE_SIZE_MEGABYTES"], out var maxCacheSizeMegabytes))
                {
                    options.MaxCacheSizeMegabytes = maxCacheSizeMegabytes;
                }

                if (int.TryParse(configuration["REVIEW_WORKSPACE_MAX_CONCURRENT_PREPARATIONS"], out var maxConcurrentPreparations))
                {
                    options.MaxConcurrentPreparations = maxConcurrentPreparations;
                }

                if (!string.IsNullOrWhiteSpace(configuration["REVIEW_WORKSPACE_FETCH_DEPTH_POLICY"]))
                {
                    options.FetchDepthPolicy = configuration["REVIEW_WORKSPACE_FETCH_DEPTH_POLICY"]!.Trim();
                }

                if (int.TryParse(configuration["REVIEW_WORKSPACE_FETCH_DEPTH"], out var fetchDepth))
                {
                    options.FetchDepth = fetchDepth;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<IReviewRepositoryWorkspaceManager, GitReviewRepositoryWorkspaceManager>();
        services.TryAddScoped<IReviewWorkspaceRemoteResolver, ProviderReviewWorkspaceRemoteResolver>();
        services.TryAddScoped<GitCommandRunner>();
        services.TryAddSingleton<ReviewWorkspaceCleanupService>();
        services.TryAddSingleton<ReviewWorkspacePreparationThrottle>();

        return services;
    }
}
