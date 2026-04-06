// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.AI.ProCursor;
using MeisterProPR.Infrastructure.AzureDevOps.ProCursor;
using MeisterProPR.Infrastructure.CodeAnalysis.ProCursor;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.ProCursor.Infrastructure.DependencyInjection;

/// <summary>
///     Extension methods for registering the ProCursor bounded module.
/// </summary>
public static class ProCursorModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers ProCursor options and future module services.
    /// </summary>
    public static IServiceCollection AddProCursorModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        services.AddOptions<ProCursorOptions>()
            .Configure(opts =>
            {
                ConfigureInt(configuration, "PROCURSOR_MAX_INDEX_CONCURRENCY", value => opts.MaxIndexConcurrency = value);
                ConfigureInt(configuration, "PROCURSOR_MAX_QUERY_RESULTS", value => opts.MaxQueryResults = value);
                ConfigureInt(configuration, "PROCURSOR_MAX_SOURCES_PER_QUERY", value => opts.MaxSourcesPerQuery = value);
                ConfigureInt(configuration, "PROCURSOR_CHUNK_TARGET_LINES", value => opts.ChunkTargetLines = value);
                ConfigureInt(configuration, "PROCURSOR_MINI_INDEX_TTL_MINUTES", value => opts.MiniIndexTtlMinutes = value);
                ConfigureInt(configuration, "PROCURSOR_REFRESH_POLL_SECONDS", value => opts.RefreshPollSeconds = value);
                ConfigureInt(configuration, "PROCURSOR_TEMP_WORKSPACE_RETENTION_MINUTES", value => opts.TempWorkspaceRetentionMinutes = value);
                ConfigureInt(configuration, "PROCURSOR_EMBEDDING_DIMENSIONS", value => opts.EmbeddingDimensions = value);
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ProCursorTokenUsageOptions>()
            .Configure(opts =>
            {
                ConfigureInt(configuration, "PROCURSOR_TOKEN_USAGE_ROLLUP_POLL_SECONDS", value => opts.RollupPollSeconds = value);
                ConfigureInt(configuration, "PROCURSOR_TOKEN_USAGE_EVENT_RETENTION_DAYS", value => opts.EventRetentionDays = value);
                ConfigureInt(configuration, "PROCURSOR_TOKEN_USAGE_ROLLUP_RETENTION_DAYS", value => opts.RollupRetentionDays = value);
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IProCursorKnowledgeSourceRepository, ProCursorKnowledgeSourceRepository>();
            services.AddScoped<IProCursorIndexJobRepository, ProCursorIndexJobRepository>();
            services.AddScoped<IProCursorIndexSnapshotRepository, ProCursorIndexSnapshotRepository>();
            services.AddScoped<ProCursorSymbolGraphRepository>();
            services.AddScoped<IProCursorSymbolGraphRepository>(sp => sp.GetRequiredService<ProCursorSymbolGraphRepository>());
        }

        services.AddScoped<IProCursorChunkExtractor, ProCursorChunkExtractor>();
        services.AddScoped<IProCursorEmbeddingService, ProCursorEmbeddingService>();
        services.AddScoped<IProCursorSymbolExtractor, RoslynProCursorSymbolExtractor>();
        services.AddScoped<ProCursorRefreshScheduler>();

        if (!configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.AddScoped<IProCursorMaterializer, AdoRepositoryMaterializer>();
            services.AddScoped<IProCursorMaterializer, AdoWikiMaterializer>();
            services.AddScoped<IProCursorTrackedBranchChangeDetector, AdoTrackedBranchChangeDetector>();
        }
        else
        {
            services.AddScoped<IProCursorTrackedBranchChangeDetector, NullProCursorTrackedBranchChangeDetector>();
        }

        services.AddScoped<ProCursorQueryService>();
        services.AddScoped<ProCursorMiniIndexBuilder>();
        services.AddScoped<ProCursorIndexCoordinator>();
        services.AddScoped<IProCursorGateway, ProCursorGateway>();

        return services;
    }

    private static void ConfigureInt(IConfiguration configuration, string key, Action<int> assign)
    {
        if (int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            assign(value);
        }
    }
}
