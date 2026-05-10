// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.AI.ProCursor;
using MeisterProPR.Infrastructure.CodeAnalysis.ProCursor;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.ProCursor.Contracts.ProCursor;
using MeisterProPR.ProCursor.Core;
using MeisterProPR.ProCursor.Infrastructure.AzureDevOps.DependencyInjection;
using MeisterProPR.ProCursor.Infrastructure.Remote;
using MeisterProPR.ProCursor.Options;
using MeisterProPR.ProCursor.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public static IServiceCollection AddProCursorModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var proCursorDbConnectionString = configuration["PROCURSOR_DB_CONNECTION_STRING"];

        services.AddOptions<ProCursorOptions>()
            .Configure(opts =>
            {
                ConfigureInt(
                    configuration,
                    "PROCURSOR_MAX_INDEX_CONCURRENCY",
                    value => opts.MaxIndexConcurrency = value);
                ConfigureInt(configuration, "PROCURSOR_MAX_QUERY_RESULTS", value => opts.MaxQueryResults = value);
                ConfigureInt(
                    configuration,
                    "PROCURSOR_MAX_SOURCES_PER_QUERY",
                    value => opts.MaxSourcesPerQuery = value);
                ConfigureInt(configuration, "PROCURSOR_CHUNK_TARGET_LINES", value => opts.ChunkTargetLines = value);
                ConfigureInt(
                    configuration,
                    "PROCURSOR_MINI_INDEX_TTL_MINUTES",
                    value => opts.MiniIndexTtlMinutes = value);
                ConfigureInt(configuration, "PROCURSOR_REFRESH_POLL_SECONDS", value => opts.RefreshPollSeconds = value);
                ConfigureInt(
                    configuration,
                    "PROCURSOR_TEMP_WORKSPACE_RETENTION_MINUTES",
                    value => opts.TempWorkspaceRetentionMinutes = value);
                ConfigureInt(
                    configuration,
                    "PROCURSOR_EMBEDDING_DIMENSIONS",
                    value => opts.EmbeddingDimensions = value);
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ProCursorTokenUsageOptions>()
            .Configure(opts =>
            {
                ConfigureInt(
                    configuration,
                    "PROCURSOR_TOKEN_USAGE_ROLLUP_POLL_SECONDS",
                    value => opts.RollupPollSeconds = value);
                ConfigureInt(
                    configuration,
                    "PROCURSOR_TOKEN_USAGE_EVENT_RETENTION_DAYS",
                    value => opts.EventRetentionDays = value);
                ConfigureInt(
                    configuration,
                    "PROCURSOR_TOKEN_USAGE_ROLLUP_RETENTION_DAYS",
                    value => opts.RollupRetentionDays = value);
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (!string.IsNullOrWhiteSpace(proCursorDbConnectionString))
        {
            services.AddDbContext<ProCursorOperationalDbContext>(
                options => options
                    .UseNpgsql(
                        proCursorDbConnectionString,
                        o => o.UseVector().MigrationsHistoryTable("__EFMigrationsHistory_ProCursor"))
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped,
                ServiceLifetime.Singleton);
            services.AddDbContextFactory<ProCursorOperationalDbContext>(options => options
                .UseNpgsql(
                    proCursorDbConnectionString,
                    o => o.UseVector().MigrationsHistoryTable("__EFMigrationsHistory_ProCursor"))
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

            services.AddScoped<IProCursorIndexJobRepository, ProCursorIndexJobRepository>();
            services.AddScoped<IProCursorIndexSnapshotRepository, ProCursorIndexSnapshotRepository>();
            services.AddScoped<ProCursorSymbolGraphRepository>();
            services.AddScoped<IProCursorSymbolGraphRepository>(sp =>
                sp.GetRequiredService<ProCursorSymbolGraphRepository>());
            services.AddSingleton<IProCursorTokenUsageRecorder, EfProCursorTokenUsageRecorder>();
            services.AddScoped<IProCursorTokenUsageReadRepository, ProCursorTokenUsageReadRepository>();
            services.AddScoped<IProCursorTokenUsageAggregationService, ProCursorTokenUsageAggregationService>();
            services.AddScoped<IProCursorTokenUsageRebuildService, ProCursorTokenUsageRebuildService>();
            services.AddScoped<IProCursorTokenUsageRetentionService, ProCursorTokenUsageRetentionService>();
        }

        services.AddHttpClient<ProPrRuntimeConfigurationBroker>((sp, httpClient) =>
        {
            var currentHostOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProCursorHostOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(currentHostOptions.ProPrBaseUrl))
            {
                httpClient.BaseAddress = new Uri(currentHostOptions.ProPrBaseUrl.TrimEnd('/') + "/");
            }

            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, currentHostOptions.RequestTimeoutSeconds));
            if (!string.IsNullOrWhiteSpace(currentHostOptions.SharedKey))
            {
                httpClient.DefaultRequestHeaders.Remove(ProCursorSharedKeyAuthenticationDefaults.HeaderName);
                httpClient.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, currentHostOptions.SharedKey);
            }
        });
        services.AddSingleton<IProCursorRuntimeConfigurationBroker>(sp => sp.GetRequiredService<ProPrRuntimeConfigurationBroker>());
        services.AddSingleton<RuntimeConfiguredKnowledgeSourceRepository>();
        services.AddSingleton<IProCursorRuntimeConfigurationCache>(sp => sp.GetRequiredService<RuntimeConfiguredKnowledgeSourceRepository>());
        services.AddScoped<IProCursorKnowledgeSourceRepository>(sp => sp.GetRequiredService<RuntimeConfiguredKnowledgeSourceRepository>());
        services.AddScoped<IProCursorChunkExtractor, ProCursorChunkExtractor>();
        services.AddScoped<IProCursorEmbeddingService, ProCursorEmbeddingService>();
        services.AddScoped<IProCursorSymbolExtractor, RoslynProCursorSymbolExtractor>();

        services.AddHttpClient<ProPrScmBroker>((sp, httpClient) =>
        {
            var hostOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProCursorHostOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(hostOptions.ProPrBaseUrl))
            {
                httpClient.BaseAddress = new Uri(hostOptions.ProPrBaseUrl.TrimEnd('/') + "/");
            }

            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, hostOptions.RequestTimeoutSeconds));

            if (!string.IsNullOrWhiteSpace(hostOptions.SharedKey))
            {
                httpClient.DefaultRequestHeaders.Remove(ProCursorSharedKeyAuthenticationDefaults.HeaderName);
                httpClient.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, hostOptions.SharedKey);
            }
        });
        services.AddHttpClient<ProPrEmbeddingBroker>((sp, httpClient) =>
        {
            var hostOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProCursorHostOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(hostOptions.ProPrBaseUrl))
            {
                httpClient.BaseAddress = new Uri(hostOptions.ProPrBaseUrl.TrimEnd('/') + "/");
            }

            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, hostOptions.RequestTimeoutSeconds));

            if (!string.IsNullOrWhiteSpace(hostOptions.SharedKey))
            {
                httpClient.DefaultRequestHeaders.Remove(ProCursorSharedKeyAuthenticationDefaults.HeaderName);
                httpClient.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, hostOptions.SharedKey);
            }
        });
        services.AddScoped<IProCursorScmBroker>(sp =>
            sp.GetRequiredService<ProPrScmBroker>());
        services.AddScoped<IProCursorEmbeddingBroker>(sp =>
            sp.GetRequiredService<ProPrEmbeddingBroker>());

        services.AddAzureDevOpsProCursorServices(configuration);
        services.AddScoped<ProCursorQueryService>();
        services.AddScoped<ProCursorMiniIndexBuilder>();
        services.AddScoped<ProCursorIndexCoordinator>();
        services.AddScoped<ProCursorGateway>();
        services.AddScoped<IProCursorGateway>(sp => sp.GetRequiredService<ProCursorGateway>());

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
