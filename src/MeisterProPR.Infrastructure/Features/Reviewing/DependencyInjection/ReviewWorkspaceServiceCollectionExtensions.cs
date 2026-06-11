// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.Infrastructure.Features.Reviewing.DependencyInjection;

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
                    options.FetchDepthPolicy = configuration["REVIEW_WORKSPACE_FETCH_DEPTH_POLICY"]!;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<IReviewRepositoryWorkspaceManager, GitReviewRepositoryWorkspaceManager>();
        services.TryAddScoped<IReviewWorkspaceRemoteResolver, ProviderReviewWorkspaceRemoteResolver>();
        services.TryAddScoped<GitCommandRunner>();
        services.TryAddSingleton<ReviewWorkspaceCleanupService>();

        return services;
    }
}
