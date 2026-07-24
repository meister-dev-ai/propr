// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

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
using MeisterProPR.Infrastructure.Net;
using MeisterProPR.Infrastructure.Options;
using MeisterProPR.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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

    /// <summary>
    ///     Returns <see langword="true" /> when a ProCursor operational database connection string is configured.
    /// </summary>
    public static bool HasProCursorOperationalDatabaseConnectionString(this IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["PROCURSOR_DB_CONNECTION_STRING"]);
    }

    /// <summary>
    ///     Resolves the ProCursor operational database connection string when explicitly configured.
    /// </summary>
    public static string? GetProCursorOperationalDatabaseConnectionString(this IConfiguration configuration)
    {
        return configuration["PROCURSOR_DB_CONNECTION_STRING"];
    }

    public static IServiceCollection AddInfrastructureSupport(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null,
        bool includeProviderOperationalServices = true)
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
        services.AddScoped<IAiRuntimeResolver, AiRuntimeResolver>();
        services.AddScoped<IAiRuntimeFactory, AiRuntimeFactory>();
        // ILogicalModelResolver is registered in the Clients module, alongside its ILogicalModelCatalogRepository
        // dependency (both gated on a database connection) — not here, so infrastructure-support-only compositions
        // (e.g. the ProCursor remote-mode host) validate without the persistence layer present.

        // ADO operational services are composed behind provider-local registration.
        if (includeProviderOperationalServices)
        {
            var adoOperationalCredential =
                configuration.GetValue<bool>("ADO_STUB_PR") ? null : ResolveCredential(configuration);
            services.AddAzureDevOpsInfrastructureServices(configuration, adoOperationalCredential);
        }

        // AiReviewOptions — bound from individual env vars (not a config section)
        services.AddOptions<AiReviewOptions>()
            .Configure(opts => ConfigureAiReviewOptions(opts, configuration));

        // Some singleton review executors consume a snapshot of the configured values directly
        // instead of the options wrapper, so expose the bound instance as a concrete service too.
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiReviewOptions>>().Value);

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

                if (int.TryParse(configuration["WORKER_MAX_CONCURRENT_REVIEW_JOBS"], out var maxConcurrentReviewJobs))
                {
                    opts.MaxConcurrentReviewJobs = maxConcurrentReviewJobs;
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

        // Per-client AI connection factory (singleton — stateless, creates new clients on demand).
        // Guard these outbound clients against SSRF: an admin-supplied AI baseUrl must not reach
        // private/loopback/link-local (incl. cloud-metadata) addresses, and redirects are never followed.
        // Private egress is permitted in Development so a local provider (e.g. LiteLLM) stays reachable, or when
        // an operator explicitly opts in via AI_ALLOW_PRIVATE_EGRESS to reach a self-hosted / on-prem endpoint.
        // Both are off by default, so production egress stays locked unless deliberately enabled.
        var isDevelopment = environment?.IsDevelopment() ?? false;
        var allowPrivateEgress = AllowPrivateEgress(isDevelopment, configuration);
        services.AddHttpClient("AiProbe")
            .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));
        services.AddHttpClient("AiProviderAdmin")
            .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));

        // Runtime chat/embedding traffic egresses through the same SSRF guard. An infinite HttpClient
        // timeout matches the SDK's default shared transport so long completions are not truncated; the
        // per-request cancellation token still bounds each call.
        services.AddHttpClient("AiProviderRuntime", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));
        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        services.AddSingleton<IAiProviderDriver, AzureOpenAiProviderDriver>();
        // The config-time probe check permits a private host when private egress is allowed, but plain http is
        // relaxed only in Development — a self-hosted endpoint reached via the opt-in must still use https.
        services.AddSingleton<IAiProviderDriver>(serviceProvider => new OpenAiProviderDriver(
            serviceProvider.GetRequiredService<OpenAiCompatibleTransport>(),
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            allowPrivateEgress,
            allowInsecureScheme: isDevelopment));
        services.AddSingleton<IAiProviderDriver>(serviceProvider => new LiteLlmProviderDriver(
            serviceProvider.GetRequiredService<OpenAiCompatibleTransport>(),
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            allowPrivateEgress,
            allowInsecureScheme: isDevelopment));
        services.AddSingleton<IAiProviderDriverRegistry, AiProviderRegistry>();
        services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();

        return services;
    }

    /// <summary>
    ///     Binds <see cref="AiReviewOptions" /> fields from individual environment variables.
    /// </summary>
    private static void ConfigureAiReviewOptions(AiReviewOptions opts, IConfiguration configuration)
    {
        opts.MaxIterations = TryGetInt(configuration, "AI_MAX_REVIEW_ITERATIONS") ?? opts.MaxIterations;
        opts.FileBatchLines = TryGetInt(configuration, "AI_FILE_BATCH_LINES") ?? opts.FileBatchLines;
        opts.ConfidenceThreshold = TryGetInt(configuration, "AI_CONFIDENCE_THRESHOLD") ?? opts.ConfidenceThreshold;
        opts.MaxFileSizeBytes = TryGetInt(configuration, "AI_MAX_FILE_SIZE_BYTES") ?? opts.MaxFileSizeBytes;
        opts.MaxFileReviewConcurrency = TryGetInt(configuration, "AI_MAX_FILE_REVIEW_CONCURRENCY") ?? opts.MaxFileReviewConcurrency;
        opts.MaxFileReviewRetries = TryGetInt(configuration, "AI_MAX_FILE_REVIEW_RETRIES") ?? opts.MaxFileReviewRetries;
        opts.MaxRateLimitRetries = TryGetInt(configuration, "AI_MAX_RATE_LIMIT_RETRIES") ?? opts.MaxRateLimitRetries;
        opts.MaxBackoffSeconds = TryGetInt(configuration, "AI_MAX_BACKOFF_SECONDS") ?? opts.MaxBackoffSeconds;
        opts.MaxIterationsLow = TryGetInt(configuration, "AI_MAX_ITERATIONS_LOW") ?? opts.MaxIterationsLow;
        opts.MaxIterationsMedium = TryGetInt(configuration, "AI_MAX_ITERATIONS_MEDIUM") ?? opts.MaxIterationsMedium;
        opts.MaxIterationsHigh = TryGetInt(configuration, "AI_MAX_ITERATIONS_HIGH") ?? opts.MaxIterationsHigh;
        opts.ConfidenceFloorError = TryGetInt(configuration, "AI_CONFIDENCE_FLOOR_ERROR") ?? opts.ConfidenceFloorError;
        opts.ConfidenceFloorWarning = TryGetInt(configuration, "AI_CONFIDENCE_FLOOR_WARNING") ?? opts.ConfidenceFloorWarning;
        opts.QualityFilterThreshold = TryGetInt(configuration, "AI_QUALITY_FILTER_THRESHOLD") ?? opts.QualityFilterThreshold;
        opts.MemoryTopN = TryGetInt(configuration, "AI_MEMORY_TOP_N") ?? opts.MemoryTopN;
        opts.MemoryMinSimilarity = TryGetFloat(configuration, "AI_MEMORY_MIN_SIMILARITY") ?? opts.MemoryMinSimilarity;
        opts.MemoryEmbeddingDimensions = TryGetInt(configuration, "AI_MEMORY_EMBEDDING_DIMENSIONS") ?? opts.MemoryEmbeddingDimensions;

        // Structural boundary resolution (feature 070).
        opts.EnableStructuralBoundaryResolution =
            TryGetBool(configuration, "AI_ENABLE_STRUCTURAL_BOUNDARY_RESOLUTION") ?? opts.EnableStructuralBoundaryResolution;
        opts.StructuralParseTimeoutMs = TryGetInt(configuration, "AI_STRUCTURAL_PARSE_TIMEOUT_MS") ?? opts.StructuralParseTimeoutMs;
        opts.MaxStructuralParseBytes = TryGetInt(configuration, "AI_MAX_STRUCTURAL_PARSE_BYTES") ?? opts.MaxStructuralParseBytes;

        // Cross-file structural reference surface.
        opts.EnableStructuralReferenceTools =
            TryGetBool(configuration, "AI_ENABLE_STRUCTURAL_REFERENCE_TOOLS") ?? opts.EnableStructuralReferenceTools;
        opts.MaxReferenceCandidateFiles = TryGetInt(configuration, "AI_MAX_REFERENCE_CANDIDATE_FILES") ?? opts.MaxReferenceCandidateFiles;
        opts.MaxReferenceResults = TryGetInt(configuration, "AI_MAX_REFERENCE_RESULTS") ?? opts.MaxReferenceResults;
        opts.MaxReferenceResultChars = TryGetInt(configuration, "AI_MAX_REFERENCE_RESULT_CHARS") ?? opts.MaxReferenceResultChars;
        opts.ReferenceResolutionTimeoutMs = TryGetInt(configuration, "AI_REFERENCE_RESOLUTION_TIMEOUT_MS") ?? opts.ReferenceResolutionTimeoutMs;

        // Cross-compaction tool-evidence retention (experimental; A/B only).
        opts.EnableRetainedToolEvidence =
            TryGetBool(configuration, "AI_ENABLE_RETAINED_TOOL_EVIDENCE") ?? opts.EnableRetainedToolEvidence;

        // Reasoning capture into recorded assistant-turn output (off by default; data-retention gate).
        opts.CaptureReasoningInProtocol =
            TryGetBool(configuration, "AI_CAPTURE_REASONING_IN_PROTOCOL") ?? opts.CaptureReasoningInProtocol;

        // Linked work items / issues in the review context.
        opts.MaxLinkedItemsInContext = TryGetInt(configuration, "AI_MAX_LINKED_ITEMS_IN_CONTEXT") ?? opts.MaxLinkedItemsInContext;
        opts.MaxLinkedItemDescriptionChars = TryGetInt(configuration, "AI_MAX_LINKED_ITEM_DESCRIPTION_CHARS") ?? opts.MaxLinkedItemDescriptionChars;
        opts.EnableLinkedItemTools = TryGetBool(configuration, "AI_ENABLE_LINKED_ITEM_TOOLS") ?? opts.EnableLinkedItemTools;
        opts.MaxLinkedItemToolCalls = TryGetInt(configuration, "AI_MAX_LINKED_ITEM_TOOL_CALLS") ?? opts.MaxLinkedItemToolCalls;
        opts.MaxLinkedItemToolResultChars = TryGetInt(configuration, "AI_MAX_LINKED_ITEM_TOOL_RESULT_CHARS") ?? opts.MaxLinkedItemToolResultChars;
        opts.LinkedItemToolTimeoutMs = TryGetInt(configuration, "AI_LINKED_ITEM_TOOL_TIMEOUT_MS") ?? opts.LinkedItemToolTimeoutMs;
    }

    private static int? TryGetInt(IConfiguration configuration, string key)
    {
        return int.TryParse(configuration[key], out var value) ? value : null;
    }

    private static float? TryGetFloat(IConfiguration configuration, string key)
    {
        return float.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool? TryGetBool(IConfiguration configuration, string key)
    {
        return bool.TryParse(configuration[key], out var value) ? value : null;
    }

    /// <summary>
    ///     Resolves whether outbound AI egress may reach private/loopback/link-local addresses. Off by default so
    ///     production stays locked against SSRF: it is permitted in Development (so a local provider stays
    ///     reachable) or when an operator explicitly opts in via <c>AI_ALLOW_PRIVATE_EGRESS</c> to reach a
    ///     self-hosted / on-prem endpoint. A missing or non-boolean value falls through to the safe default.
    /// </summary>
    internal static bool AllowPrivateEgress(bool isDevelopment, IConfiguration configuration)
    {
        return isDevelopment || (TryGetBool(configuration, "AI_ALLOW_PRIVATE_EGRESS") ?? false);
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
