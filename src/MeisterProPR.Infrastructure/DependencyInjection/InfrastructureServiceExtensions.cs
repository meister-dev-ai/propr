using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.AzureDevOps;
using MeisterProPR.Infrastructure.AzureDevOps.Stub;
using MeisterProPR.Infrastructure.Configuration;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Options;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Repositories.Stub;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ApplicationIAiReviewCore = MeisterProPR.Application.Interfaces.IAiReviewCore;
using NoOpAdoCommentPoster = MeisterProPR.Infrastructure.AzureDevOps.Stub.NoOpAdoCommentPoster;
using StubActivePrFetcher = MeisterProPR.Infrastructure.AzureDevOps.Stub.StubActivePrFetcher;
using StubAdoThreadClient = MeisterProPR.Infrastructure.AzureDevOps.Stub.StubAdoThreadClient;
using StubAdoThreadReplier = MeisterProPR.Infrastructure.AzureDevOps.Stub.StubAdoThreadReplier;
using StubPullRequestFetcher = MeisterProPR.Infrastructure.AzureDevOps.Stub.StubPullRequestFetcher;

namespace MeisterProPR.Infrastructure.DependencyInjection;

/// <summary>
///     Extension methods for registering infrastructure services.
///     When <c>DB_CONNECTION_STRING</c> is set, PostgreSQL-backed implementations are used.
///     When not set, in-memory implementations are used (legacy/dev/test fallback).
/// </summary>
public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var dbConnectionString = configuration["DB_CONNECTION_STRING"];
        var isDbMode = !string.IsNullOrWhiteSpace(dbConnectionString);

        if (isDbMode)
        {
            // PostgreSQL mode: EF Core + Npgsql
            services.AddDbContext<MeisterProPRDbContext>(options =>
                options
                    .UseNpgsql(dbConnectionString)
                    // EF tools 9.x generate snapshots that EF runtime 10.x flags as pending;
                    // the schema is correct — suppress the spurious warning.
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

            // Protocol recorder uses a factory so it can open short-lived contexts per event write.
            services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                options
                    .UseNpgsql(dbConnectionString)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

            services.AddScoped<IJobRepository, JobRepository>();
            services.AddScoped<IClientRegistry, DbClientRegistry>();
            services.AddScoped<IClientAdminService, ClientAdminService>();
            services.AddScoped<ICrawlConfigurationRepository, CrawlConfigurationRepository>();
            services.AddScoped<IClientAdoCredentialRepository, ClientAdoCredentialRepository>();
            services.AddScoped<IMentionReplyJobRepository, EfMentionReplyJobRepository>();
            services.AddScoped<IMentionScanRepository, EfMentionScanRepository>();
            services.AddScoped<IReviewPrScanRepository, EfReviewPrScanRepository>();
            services.AddSingleton<IProtocolRecorder, EfProtocolRecorder>();

            // User auth repositories (DB mode only)
            services.AddScoped<IUserRepository, AppUserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IUserPatRepository, UserPatRepository>();

            // Auth services
            services.AddSingleton<IPasswordHashService, PasswordHashService>();
            services.AddSingleton<IJwtTokenService, JwtTokenService>();
            services.AddTransient<AdminBootstrapService>();
        }
        else
        {
            // Legacy in-memory mode (dev / WebApplicationFactory tests without DB)
            services.AddSingleton<IJobRepository, InMemoryJobRepository>();
            services.AddSingleton<IClientRegistry, EnvVarClientRegistry>();
            services.AddSingleton<IClientAdoCredentialRepository, NullClientAdoCredentialRepository>();
            services.AddSingleton<IReviewPrScanRepository, NullReviewPrScanRepository>();
            services.AddSingleton<IProtocolRecorder, NullProtocolRecorder>();
        }

        // ADO token validation (identity verification only).
        // Set ADO_SKIP_TOKEN_VALIDATION=true in user secrets to bypass the real
        // VSS endpoint during local development / testbed usage.
        if (configuration.GetValue<bool>("ADO_SKIP_TOKEN_VALIDATION"))
        {
            services.AddSingleton<IAdoTokenValidator, PassThroughAdoTokenValidator>();
        }
        else
        {
            // Credential is needed for server-side JWT validation regardless of ADO_STUB_PR.
            var validatorCredential = ResolveCredential(configuration);
            services.AddHttpClient("AdoTokenValidator");
            services.AddSingleton<IAdoTokenValidator>(sp =>
                new AdoTokenValidator(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    validatorCredential,
                    sp.GetRequiredService<ILogger<AdoTokenValidator>>()));
        }

        // ADO operations
        // Set ADO_STUB_PR=true in user secrets to use a fake PR and skip ADO comment posting
        // during local development. The real AI endpoint is still called.
        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.AddScoped<IPullRequestFetcher, StubPullRequestFetcher>();
            services.AddScoped<IAdoCommentPoster, NoOpAdoCommentPoster>();
            services.AddScoped<IAssignedPrFetcher, StubAssignedPrFetcher>();
            services.AddScoped<IIdentityResolver, StubIdentityResolver>();
            services.AddSingleton<IAdoReviewerManager, StubAdoReviewerManager>();
            services.AddSingleton<IActivePrFetcher, StubActivePrFetcher>();
            services.AddScoped<IAdoThreadReplier, StubAdoThreadReplier>();
            services.AddScoped<IAdoThreadClient, StubAdoThreadClient>();
        }
        else
        {
            var credential = ResolveCredential(configuration);
            services.AddSingleton<VssConnectionFactory>(_ => new VssConnectionFactory(credential));
            services.AddScoped<IPullRequestFetcher, AdoPrFetcher>();
            services.AddScoped<IAdoCommentPoster, AdoCommentPoster>();
            services.AddScoped<IAssignedPrFetcher, AdoAssignedPrFetcher>();
            services.AddHttpClient("AdoIdentity");
            services.AddScoped<IIdentityResolver>(sp =>
                new AdoIdentityResolver(
                    credential,
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IClientAdoCredentialRepository>()));
            services.AddSingleton<IAdoReviewerManager, AdoReviewerManager>();
            services.AddSingleton<IActivePrFetcher, AdoActivePrFetcher>();
            services.AddScoped<IAdoThreadReplier, AdoThreadReplier>();
            services.AddScoped<IAdoThreadClient, AdoThreadClient>();
        }

        // AI review (provider-agnostic via IChatClient)
        var aiEndpoint = configuration["AI_ENDPOINT"]
                         ?? throw new InvalidOperationException("AI_ENDPOINT environment variable is not set.");
        var aiDeployment = configuration["AI_DEPLOYMENT"]
                           ?? throw new InvalidOperationException("AI_DEPLOYMENT environment variable is not set.");

        services.AddKeyedSingleton<IChatClient>("base", (_, _) => CreateChatClient(
            aiEndpoint,
            aiDeployment,
            configuration["AI_API_KEY"]));

        services.AddSingleton<IChatClient>(sp => new ResilientChatClientDecorator(
            sp.GetRequiredKeyedService<IChatClient>("base"),
            sp.GetRequiredService<IOptions<AiReviewOptions>>(),
            sp.GetRequiredService<ILogger<ResilientChatClientDecorator>>()));

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
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // WorkerOptions — bound from individual env vars
        services.AddOptions<WorkerOptions>()
            .Configure(opts =>
            {
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
                    CreateChatClient(evaluatorEndpoint, evaluatorDeployment, configuration["AI_API_KEY"]));
        }

        services.AddSingleton<ApplicationIAiReviewCore, ToolAwareAiReviewCore>();
        services.AddSingleton<IFileByFileReviewOrchestrator, FileByFileReviewOrchestrator>();
        services.AddSingleton<IAiCommentResolutionCore, AgentAiCommentResolutionCore>();
        services.AddSingleton<IMentionAnswerService, AgentMentionAnswerService>();

        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.AddSingleton<IReviewContextToolsFactory, StubReviewContextToolsFactory>();
            services.AddSingleton<IRepositoryInstructionFetcher, NullRepositoryInstructionFetcher>();
        }
        else
        {
            services.AddSingleton<IReviewContextToolsFactory>(sp =>
                new AdoReviewContextToolsFactory(
                    sp.GetRequiredService<VssConnectionFactory>(),
                    sp.GetRequiredService<IClientAdoCredentialRepository>(),
                    sp.GetRequiredService<IOptions<AiReviewOptions>>()));
            services.AddSingleton<IRepositoryInstructionFetcher, AdoRepositoryInstructionFetcher>();
        }

        // Register instruction evaluator: use AI evaluator when endpoint is configured, otherwise pass-through
        if (!string.IsNullOrWhiteSpace(evaluatorEndpoint) && !string.IsNullOrWhiteSpace(evaluatorDeployment))
        {
            services.AddSingleton<IRepositoryInstructionEvaluator, AiRepositoryInstructionEvaluator>();
        }
        else
        {
            services.AddSingleton<IRepositoryInstructionEvaluator, PassThroughRepositoryInstructionEvaluator>();
        }

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
    private static IChatClient CreateChatClient(string endpoint, string deployment, string? apiKey)
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
        return azureClient.GetResponsesClient(deployment).AsIChatClient();
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
