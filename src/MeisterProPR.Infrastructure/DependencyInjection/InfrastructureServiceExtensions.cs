// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.Globalization;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.AI.OpenAiCompatible;
using MeisterProPR.Infrastructure.AI.Providers;
using MeisterProPR.Infrastructure.AI.Providers.AzureOpenAi;
using MeisterProPR.Infrastructure.AI.Providers.LiteLlm;
using MeisterProPR.Infrastructure.AI.Providers.OpenAi;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterProPR.Infrastructure.Options;
using MeisterProPR.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.DependencyInjection;

/// <summary>
///     Extension methods for registering infrastructure services.
///     PostgreSQL-backed implementations are used when <c>DB_CONNECTION_STRING</c> is configured.
/// </summary>
public static class InfrastructureServiceExtensions
{
    /// <summary>
    ///     Returns <see langword="true" /> when a database connection string is configured.
    /// </summary>
    public static bool HasDatabaseConnectionString(this IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["DB_CONNECTION_STRING"]);
    }

    public static IServiceCollection AddInfrastructureSupport(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var dbConnectionString = configuration["DB_CONNECTION_STRING"];
        var hasDatabaseConnectionString = configuration.HasDatabaseConnectionString();

        if (hasDatabaseConnectionString)
        {
            // PostgreSQL mode: EF Core + Npgsql
            services.AddDbContext<MeisterProPRDbContext>(
                options =>
                    options
                        .UseNpgsql(dbConnectionString, o => o.UseVector())
                        // EF tools 9.x generate snapshots that EF runtime 10.x flags as pending;
                        // the schema is correct — suppress the spurious warning.
                        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped,
                ServiceLifetime.Singleton);

            // Protocol recorder uses a factory so it can open short-lived contexts per event write.
            services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                options
                    .UseNpgsql(dbConnectionString, o => o.UseVector())
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        }

        services.AddSingleton<ISecretProtectionCodec, SecretProtectionCodec>();
        services.AddScoped<EmbeddingDeploymentResolver>();
        services.AddScoped<IAiRuntimeResolver, AiRuntimeResolver>();

        // ADO operational services are composed behind provider-local registration.
        var adoOperationalCredential =
            configuration.GetValue<bool>("ADO_STUB_PR") ? null : ResolveCredential(configuration);
        services.AddAzureDevOpsInfrastructureServices(configuration, adoOperationalCredential);

        // AiReviewOptions — bound from individual env vars (not a config section)
        services.AddOptions<AiReviewOptions>()
            .Configure(opts =>
            {
                if (int.TryParse(configuration["AI_MAX_REVIEW_ITERATIONS"], out var maxIter))
                {
                    opts.MaxIterations = maxIter;
                }

                if (int.TryParse(configuration["AI_FILE_BATCH_LINES"], out var batchLines))
                {
                    opts.FileBatchLines = batchLines;
                }

                if (int.TryParse(configuration["AI_CONFIDENCE_THRESHOLD"], out var threshold))
                {
                    opts.ConfidenceThreshold = threshold;
                }

                if (int.TryParse(configuration["AI_MAX_FILE_SIZE_BYTES"], out var maxSize))
                {
                    opts.MaxFileSizeBytes = maxSize;
                }

                if (int.TryParse(configuration["AI_MAX_FILE_REVIEW_CONCURRENCY"], out var concurrency))
                {
                    opts.MaxFileReviewConcurrency = concurrency;
                }

                if (int.TryParse(configuration["AI_MAX_FILE_REVIEW_RETRIES"], out var retries))
                {
                    opts.MaxFileReviewRetries = retries;
                }

                if (int.TryParse(configuration["AI_MAX_RATE_LIMIT_RETRIES"], out var rateLimitRetries))
                {
                    opts.MaxRateLimitRetries = rateLimitRetries;
                }

                if (int.TryParse(configuration["AI_MAX_BACKOFF_SECONDS"], out var backoff))
                {
                    opts.MaxBackoffSeconds = backoff;
                }

                if (int.TryParse(configuration["AI_MAX_ITERATIONS_LOW"], out var maxIterLow))
                {
                    opts.MaxIterationsLow = maxIterLow;
                }

                if (int.TryParse(configuration["AI_MAX_ITERATIONS_MEDIUM"], out var maxIterMedium))
                {
                    opts.MaxIterationsMedium = maxIterMedium;
                }

                if (int.TryParse(configuration["AI_MAX_ITERATIONS_HIGH"], out var maxIterHigh))
                {
                    opts.MaxIterationsHigh = maxIterHigh;
                }

                if (int.TryParse(configuration["AI_CONFIDENCE_FLOOR_ERROR"], out var confidenceFloorError))
                {
                    opts.ConfidenceFloorError = confidenceFloorError;
                }

                if (int.TryParse(configuration["AI_CONFIDENCE_FLOOR_WARNING"], out var confidenceFloorWarning))
                {
                    opts.ConfidenceFloorWarning = confidenceFloorWarning;
                }

                if (int.TryParse(configuration["AI_QUALITY_FILTER_THRESHOLD"], out var qualityFilterThreshold))
                {
                    opts.QualityFilterThreshold = qualityFilterThreshold;
                }

                if (int.TryParse(configuration["AI_MEMORY_TOP_N"], out var memTopN))
                {
                    opts.MemoryTopN = memTopN;
                }

                if (float.TryParse(
                        configuration["AI_MEMORY_MIN_SIMILARITY"],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var memMinSim))
                {
                    opts.MemoryMinSimilarity = memMinSim;
                }

                if (int.TryParse(configuration["AI_MEMORY_EMBEDDING_DIMENSIONS"], out var memDims))
                {
                    opts.MemoryEmbeddingDimensions = memDims;
                }
            });

        // WorkerOptions — bound from individual env vars
        services.AddOptions<WorkerOptions>()
            .Configure(opts =>
            {
                if (int.TryParse(configuration["WORKER_POLL_INTERVAL_MILLISECONDS"], out var pollIntervalMilliseconds))
                {
                    opts.PollIntervalMilliseconds = pollIntervalMilliseconds;
                }

                if (int.TryParse(configuration["WORKER_STUCK_JOB_TIMEOUT_MINUTES"], out var timeout))
                {
                    opts.StuckJobTimeoutMinutes = timeout;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AiEvaluatorOptions — bound from individual env vars; only validated and registered when both are provided.
        var evaluatorEndpoint = configuration["AI_EVALUATOR_ENDPOINT"];
        var evaluatorDeployment = configuration["AI_EVALUATOR_DEPLOYMENT"];
        if (!string.IsNullOrWhiteSpace(evaluatorEndpoint) && !string.IsNullOrWhiteSpace(evaluatorDeployment))
        {
            services.AddOptions<AiEvaluatorOptions>()
                .Configure(opts =>
                {
                    opts.Endpoint = evaluatorEndpoint;
                    opts.Deployment = evaluatorDeployment;
                })
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Keyed IChatClient for the instruction relevance evaluator
            services.AddKeyedSingleton<IChatClient>(
                "evaluator",
                (_, _) =>
                    CreateChatClient(evaluatorEndpoint, configuration["AI_API_KEY"]));
        }

        services.AddSingleton<IAiEmbeddingGeneratorFactory, AiEmbeddingGeneratorFactory>();

        // Per-client AI connection factory (singleton — stateless, creates new clients on demand)
        services.AddHttpClient("AiProbe")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Probe requests are outbound only — standard TLS validation applies.
            });
        services.AddHttpClient("AiProviderAdmin")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Provider admin operations are outbound only — standard TLS validation applies.
            });
        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        services.AddSingleton<IAiProviderDriver, AzureOpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, OpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, LiteLlmProviderDriver>();
        services.AddSingleton<IAiProviderDriverRegistry, AiProviderRegistry>();
        services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();

        return services;
    }

    /// <summary>
    ///     Creates an <see cref="IChatClient" /> backed by the Azure OpenAI <b>Responses API</b>,
    ///     which supports reasoning models, tool use, and multi-turn state.
    ///     Both <c>*.openai.azure.com</c> and <c>*.services.ai.azure.com</c> (Azure AI Foundry)
    ///     are supported via <see cref="AzureOpenAIClient" />. For AI Foundry endpoints any
    ///     project path is stripped — <see cref="AzureOpenAIClient" /> constructs the correct
    ///     <c>/openai/responses</c> sub-path from the resource root automatically.
    /// </summary>
    private static IChatClient CreateChatClient(string endpoint, string? apiKey)
    {
        var uri = new Uri(endpoint);

        // Azure AI Foundry portal URLs include a project path (.../api/projects/{project})
        // that is not part of the Azure OpenAI API surface — use only the resource root.
        if (uri.Host.EndsWith("services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri($"{uri.Scheme}://{uri.Host}/");
        }

        // Reasoning models can take several minutes to generate a response.
        // The default NetworkTimeout of 100 s is too short — raise it to 10 min.
        var options = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };

        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(uri, new DefaultAzureCredential(), options)
            : new AzureOpenAIClient(uri, new ApiKeyCredential(apiKey), options);

        // GetResponsesClient targets the Responses API endpoint instead of the
        // legacy Chat Completions endpoint, enabling reasoning and tool use.
        return azureClient.GetResponsesClient().AsIChatClient();
    }


    /// <summary>
    ///     Resolves an Azure credential from configuration. Uses <see cref="ClientSecretCredential" />
    ///     when AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_CLIENT_SECRET are present in configuration
    ///     (e.g. user secrets), otherwise falls back to <see cref="DefaultAzureCredential" /> which
    ///     picks up Azure CLI login, managed identity, etc.
    /// </summary>
    private static TokenCredential ResolveCredential(IConfiguration configuration)
    {
        var clientId = configuration["AZURE_CLIENT_ID"];
        var tenantId = configuration["AZURE_TENANT_ID"];
        var clientSecret = configuration["AZURE_CLIENT_SECRET"];

        if (!string.IsNullOrWhiteSpace(clientId) &&
            !string.IsNullOrWhiteSpace(tenantId) &&
            !string.IsNullOrWhiteSpace(clientSecret))
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        return new DefaultAzureCredential();
    }
}
