// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using Azure.Core;
using Azure.Identity;
using OpenAI;
using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.Ai.Providers.Transport;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Options;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.DependencyInjection;

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
                        // NOSONAR — EF tools 9.x generate snapshots that EF runtime 10.x flags as pending.
                        // The schema is correct, so the spurious warning is suppressed.
                        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped,
                ServiceLifetime.Singleton);

            // Protocol recorder uses a factory so it can open short-lived contexts per event write.
            services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                options
                    .UseNpgsql(dbConnectionString, o => o.UseVector())
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

            // The primitives a provider add-in reaches the host through. Registered here because every one of
            // them needs a database, and singletons because none of them holds a context: each opens one from
            // the factory per call, and that lets an add-in keep the set for the life of a client the host
            // built and what keeps a review's parallel passes from driving one context at once.
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IProviderAddInCapabilityGate, ProviderAddInCapabilityGate>();
            services.AddSingleton<IProviderConnectionOwnerRoles, ProviderConnectionOwnerRoles>();
            services.AddSingleton<ProviderActionInvocations>();
            services.AddSingleton<ProviderCredentialHealthStore>();

            // Disposed with the container, and that trips the signal of every action still in flight when
            // the host stops.
            services.AddSingleton<ProviderInvocationCancellation>();
            services.AddSingleton<ProviderConnectionContextFactory>();
            services.AddSingleton<IProviderConnectionContextFactory>(sp => sp.GetRequiredService<ProviderConnectionContextFactory>());
            services.AddSingleton<ProviderActionDispatcher>();

            // Registered here with the rest of the database-backed set, because activating an add-in writes the
            // decision as a row and reads the administrator's name from the installation.
            services.AddSingleton<ProviderAddInActivationService>();

            // Runs while the host is stopping and before the container is disposed, so the runs it expires are
            // still listed as open at the moment it reads them.
            services.AddHostedService<ProviderInvocationShutdownExpiry>();
        }

        services.AddSingleton<ISecretProtectionCodec, SecretProtectionCodec>();

        // One meter for the process, so the instruments are not recreated per resolved runtime. Its name matches
        // the meter the host already exports, so no telemetry configuration has to learn about it.
        services.AddSingleton<AiProviderMetrics>();

        // One gate for the process, because its whole job is to tell calls about a throttle that happened
        // somewhere else. A scoped one would be a private note each review writes to itself. Every provider
        // request is issued from the API process, runner-executed reviews included because a runner relays its
        // model calls back through it, so one gate covers the whole fan-out of one replica. A deployment running
        // several API replicas has a gate per replica, and each learns about a throttle on its own.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new ProviderThrottleGate(sp.GetRequiredService<TimeProvider>()));
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

                // Retired, and accepted rather than rejected so an existing deployment still starts. The
                // worker reports it at startup so the operator learns it no longer does anything.
                if (int.TryParse(configuration["WORKER_STUCK_JOB_TIMEOUT_MINUTES"], out var retiredTimeout))
                {
                    opts.RetiredStuckJobTimeoutMinutes = retiredTimeout;
                }

                if (int.TryParse(configuration["WORKER_MAX_CONCURRENT_REVIEW_JOBS"], out var maxConcurrentReviewJobs))
                {
                    opts.MaxConcurrentReviewJobs = maxConcurrentReviewJobs;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // ReviewLeaseOptions, bound from individual env vars. Validated on start because a lease shorter
        // than a few heartbeat intervals hands healthy jobs to another host.
        services.AddOptions<ReviewLeaseOptions>()
            .Configure(opts =>
            {
                if (int.TryParse(configuration["REVIEW_LEASE_DURATION_SECONDS"], out var leaseDuration))
                {
                    opts.LeaseDurationSeconds = leaseDuration;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_HEARTBEAT_INTERVAL_SECONDS"], out var heartbeatInterval))
                {
                    opts.HeartbeatIntervalSeconds = heartbeatInterval;
                }

                if (double.TryParse(
                        configuration["REVIEW_LEASE_HEARTBEAT_JITTER_FRACTION"],
                        CultureInfo.InvariantCulture,
                        out var jitterFraction))
                {
                    opts.HeartbeatJitterFraction = jitterFraction;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_MAX_HEARTBEAT_FAILURES"], out var maxFailures))
                {
                    opts.MaxConsecutiveHeartbeatFailures = maxFailures;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_CLAIM_CANDIDATE_LIMIT"], out var candidateLimit))
                {
                    opts.ClaimCandidateLimit = candidateLimit;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_MAX_CONSECUTIVE_RECLAIMS"], out var maxConsecutive))
                {
                    opts.MaxConsecutiveReclaims = maxConsecutive;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_MAX_TOTAL_RECLAIMS"], out var maxTotal))
                {
                    opts.MaxTotalReclaims = maxTotal;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_RECLAIM_BACKOFF_SECONDS"], out var reclaimBackoff))
                {
                    opts.ReclaimBackoffSeconds = reclaimBackoff;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_MAX_RECLAIMS_PER_SWEEP"], out var maxPerSweep))
                {
                    opts.MaxReclaimsPerSweep = maxPerSweep;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_RECLAIM_SWEEP_INTERVAL_SECONDS"], out var sweepInterval))
                {
                    opts.ReclaimSweepIntervalSeconds = sweepInterval;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_PUBLICATION_TIMEOUT_MINUTES"], out var publicationTimeout))
                {
                    opts.PublicationTimeoutMinutes = publicationTimeout;
                }

                if (int.TryParse(configuration["REVIEW_LEASE_MAX_REVIEW_DURATION_MINUTES"], out var maxReviewDuration))
                {
                    opts.MaxReviewDurationMinutes = maxReviewDuration;
                }

                if (!string.IsNullOrWhiteSpace(configuration["RUNNER_ADVERTISED_URL"]))
                {
                    opts.AdvertisedRunnerUrl = configuration["RUNNER_ADVERTISED_URL"];
                }
            })
            .ValidateDataAnnotations()
            .Validate(
                opts => opts.LeaseDurationSeconds
                        >= opts.HeartbeatIntervalSeconds * ReviewLeaseOptions.MinimumHeartbeatsPerLease,
                $"REVIEW_LEASE_DURATION_SECONDS must be at least {ReviewLeaseOptions.MinimumHeartbeatsPerLease} "
                + "times REVIEW_LEASE_HEARTBEAT_INTERVAL_SECONDS so a single late renewal cannot lose a healthy lease.")
            .ValidateOnStart();

        // RunnerFleetOptions: what counts as a live fleet, and when an idle queue counts as a stall.
        services.AddOptions<RunnerFleetOptions>()
            .Configure(opts =>
            {
                if (int.TryParse(configuration["RUNNER_ACTIVE_HEARTBEAT_WINDOW_SECONDS"], out var activeWindow))
                {
                    opts.ActiveHeartbeatWindowSeconds = activeWindow;
                }

                if (int.TryParse(configuration["RUNNER_FLEET_EMPTY_SETTLE_SECONDS"], out var settle))
                {
                    opts.FleetEmptySettleSeconds = settle;
                }

                if (int.TryParse(configuration["RUNNER_QUEUE_STALL_GRACE_SECONDS"], out var stallGrace))
                {
                    opts.QueueStallGraceSeconds = stallGrace;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The one rule that spans two option sets, so neither one's own range check can catch it.
        services.AddSingleton<IValidateOptions<RunnerFleetOptions>, RunnerFleetOptionsValidator>();

        // RunnerIngestOptions: bounds on what an executor may send in one batch.
        services.AddOptions<RunnerIngestOptions>()
            .Configure(opts =>
            {
                if (int.TryParse(configuration["RUNNER_INGEST_MAX_ITEMS_PER_BATCH"], out var maxItems))
                {
                    opts.MaxItemsPerBatch = maxItems;
                }

                if (int.TryParse(configuration["RUNNER_INGEST_MAX_BATCH_BYTES"], out var maxBytes))
                {
                    opts.MaxBatchBytes = maxBytes;
                }

                if (int.TryParse(configuration["RUNNER_INGEST_FRESHNESS_SECONDS"], out var freshness))
                {
                    opts.FreshnessSeconds = freshness;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AiEvaluatorOptions — bound from individual env vars; only validated and registered when both are provided.
        // Resolved before the evaluator client is built, because that client takes the same guarded transport.
        var isDevelopment = environment?.IsDevelopment() ?? false;
        var allowPrivateEgress = AllowPrivateEgress(isDevelopment, configuration);

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
                    CreateChatClient(evaluatorEndpoint, configuration["AI_API_KEY"], allowPrivateEgress));
        }

        // Guard these outbound clients against SSRF: an admin-supplied AI baseUrl must not reach
        // private/loopback/link-local (incl. cloud-metadata) addresses, and redirects are never followed.
        // Private egress is permitted in Development so a local provider (e.g. LiteLLM) stays reachable, or when
        // an operator explicitly opts in via AI_ALLOW_PRIVATE_EGRESS to reach a self-hosted / on-prem endpoint.
        // Both are off by default, so production egress stays locked unless deliberately enabled.

        // Resolved once and registered, so every place that checks an operator-entered address against this
        // installation's rules — the probe target a provider family is handed, the connection write paths, the
        // declared-field floor — asks the same question of the same answer instead of reading the environment
        // again.
        services.AddSingleton(new EgressUrlPolicy(allowPrivateEgress, AllowInsecureScheme: isDevelopment));
        services.AddHttpClient(ProviderHttpPipelines.Probe)
            .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));
        services.AddHttpClient(ProviderHttpPipelines.Admin)
            .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));

        // Runtime chat/embedding traffic egresses through the same SSRF guard. An infinite HttpClient
        // timeout matches the SDK's default shared transport so long completions are not truncated; the
        // per-request cancellation token still bounds each call.
        // The reasoning round-trip sits above the egress guard: it rewrites bodies, which is only meaningful for
        // a request that is allowed out in the first place. It configures itself from the wire, so providers that
        // never send the field pay one substring check per response.
        // The finish-reason repair sits below it, nearest the wire, so the response is already readable by the time
        // anything else inspects it. It applies to every OpenAI-shaped provider on purpose: the value it corrects is
        // one the client library cannot parse at all, so a conforming provider never reaches its rewrite, and a
        // proxy that starts forwarding a non-conforming upstream is covered without needing to be enumerated here.
        services.AddHttpClient(
                ProviderHttpPipelines.Runtime,
                client => client.Timeout = ProviderHttpPipelines.RuntimeTimeout)
            .AddHttpMessageHandler(() => new ReasoningContentRoundTripHandler())
            .AddHttpMessageHandler(() => new FinishReasonNormalizingHandler())
            .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));
        // The only way a provider family supplied by an add-in reaches the network. Registered beside the
        // pipelines rather than with the primitives that act on a stored connection, because it needs no
        // database: a family answering for values an operator has typed has no connection yet and still has to
        // call the endpoint they entered.
        services.AddSingleton<IProviderHttpClientFactory, ProviderHttpClientFactory>();
        // No provider family is registered here. Every one of them is supplied by an add-in in the built-in
        // directory, which the pass below reads; the loader skips an add-in claiming a family a compiled-in
        // driver already serves, so a registration here would keep the add-in out.
        // One discovery pass, held as a singleton so the directories are read once while the host starts and
        // never rescanned. The drivers it finds are deliberately not registered as IAiProviderDriver: the
        // registry refuses a second driver for one family by throwing, which is right for a composition the
        // team compiled and wrong for a file an operator dropped in a directory. The loader is given the
        // compiled-in drivers instead, so an add-in claiming one of their identities is recorded as a duplicate
        // and the host still starts.
        // Which add-in binaries an administrator has allowed this host to run. Read before the discovery pass,
        // because the pass asks for every external candidate and an unactivated one is described from its file
        // rather than loaded.
        // Starts empty and stays empty until the startup step that runs after the schema is up to date fills it.
        // The discovery pass below reads no database for the same reason it runs where it does: a composition
        // error has to be reported before a live installation's schema is touched. Every external add-in is
        // therefore described and none is loaded during composition.
        services.AddSingleton(_ => new ProviderAddInActivations([]));
        services.AddSingleton<IProviderAddInActivations>(serviceProvider =>
            serviceProvider.GetRequiredService<ProviderAddInActivations>());
        services.AddSingleton(serviceProvider => ProviderAddInLoader.Load(
            ProviderAddInDirectories.Resolve(
                configuration[ProviderAddInDirectories.ExternalDirectoryKey],
                environment?.ContentRootPath ?? AppContext.BaseDirectory),
            serviceProvider.GetServices<IAiProviderDriver>(),
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProviderAddInLoader)),
            serviceProvider.GetRequiredService<IProviderAddInActivations>()));
        // Held as the concrete type as well, because activating an add-in after the host has started adds a
        // driver to this one instance and the interface has no such member: nothing serving a review can add a
        // family, and the one path that can is the administrator's.
        services.AddSingleton(serviceProvider => new AiProviderRegistry(
        [
            .. serviceProvider.GetServices<IAiProviderDriver>(),
            .. serviceProvider.GetRequiredService<ProviderAddInCatalog>().Drivers,
        ]));
        services.AddSingleton<IAiProviderDriverRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AiProviderRegistry>());

        return services;
    }

    /// <summary>
    ///     Binds <see cref="AiReviewOptions" /> fields from individual environment variables.
    /// </summary>
    private static void ConfigureAiReviewOptions(AiReviewOptions opts, IConfiguration configuration)
    {
        // Bound by the pipeline's own binder, so a runner reads the same variables into the same fields.
        AiReviewOptionsBinder.Bind(opts, configuration);
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
    ///     which supports reasoning models, tool use, and multi-turn state. Both
    ///     <c>*.openai.azure.com</c> and <c>*.services.ai.azure.com</c> (Azure AI Foundry) are reached at the
    ///     resource's OpenAI-compatible <c>/openai/v1/</c> surface, so any project path an AI Foundry portal URL
    ///     carries is dropped and only the resource root is kept.
    /// </summary>
    private static IChatClient CreateChatClient(string endpoint, string? apiKey, bool allowPrivateEgress)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        var surface = new Uri($"{uri.Scheme}://{uri.Authority}/openai/v1/");

        // A key goes in the header the resource reads it from; with none stored, a token is minted per request
        // from the Azure credential chain.
        AuthenticationPolicy authentication = string.IsNullOrWhiteSpace(apiKey)
            ? new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default")
            : ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(new ApiKeyCredential(apiKey), "api-key");

        // Reasoning models can take several minutes to generate a response.
        // The default NetworkTimeout of 100 s is too short — raise it to 10 min.
        var options = new OpenAIClientOptions
        {
            Endpoint = surface,
            NetworkTimeout = TimeSpan.FromMinutes(10),

            // The same guarded transport every provider pipeline uses. Left to the SDK's default, this client
            // reached whatever AI_EVALUATOR_ENDPOINT named, including a loopback or a cloud-metadata address,
            // and followed redirects the rest of the host refuses.
            Transport = new HttpClientPipelineTransport(new HttpClient(GuardedEgressHttpHandler.Create(allowPrivateEgress))),
        };

        // The Responses API is targeted instead of the legacy Chat Completions endpoint, enabling reasoning and
        // tool use. The deployment name is supplied per request through ChatOptions.ModelId.
        return new OpenAIClient(authentication, options).GetResponsesClient().AsIChatClient();
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
