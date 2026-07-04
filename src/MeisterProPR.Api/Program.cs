// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using FluentValidation;
using MeisterProPR.Api.Controllers;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Clients.Controllers;
using MeisterProPR.Api.Features.Crawling.Webhooks.Validators;
using MeisterProPR.Api.Features.IdentityAndAccess.Validators;
using MeisterProPR.Api.Features.Licensing;
using MeisterProPR.Api.Features.ProCursor;
using MeisterProPR.Api.Features.ProCursor.Broker.Auth;
using MeisterProPR.Api.Features.ProCursor.Broker.Services;
using MeisterProPR.Api.HealthChecks;
using MeisterProPR.Api.Middleware;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Api.Validators;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Clients;
using MeisterProPR.Infrastructure.Features.Crawling;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.Licensing;
using MeisterProPR.Infrastructure.Features.Mentions;
using MeisterProPR.Infrastructure.Features.ProCursor.Broker;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.PromptCustomization;
using MeisterProPR.Infrastructure.Features.ReviewArchive;
using MeisterProPR.Infrastructure.Features.Reviewing;
using MeisterProPR.Infrastructure.Features.Reviewing.DependencyInjection;
using MeisterProPR.Infrastructure.Features.UsageReporting;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FeatureManagement;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.Grafana.Loki;
using Swashbuckle.AspNetCore.Swagger;
using IPNetwork = System.Net.IPNetwork;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    AppContext.SetSwitch("System.Net.Security.UseManagedNtlm", true);

    if (await TryRunHealthCheckAsync(args))
    {
        return;
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddHttpContextAccessor();

    // Ensure user-secrets are part of the application's IConfiguration in Development
    // so values can come from env vars, user secrets, or appsettings.
    if (builder.Environment.IsDevelopment())
    {
        builder.Configuration.AddUserSecrets<Program>(true);
        builder.Configuration.AddEnvironmentVariables();
    }

    var hasDatabaseConnectionString = builder.Configuration.HasDatabaseConnectionString();
    var isTesting = builder.Environment.IsEnvironment("Testing");
    var disableHostedServices = builder.Configuration.GetValue<bool>("MEISTER_DISABLE_HOSTED_SERVICES");

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        static string? RedactSecret(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? value : "[REDACTED]";
        }

        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Meister DEV ProPR")
            // Scrub secrets from log output: X-Ado-Token, X-User-Pat, AZURE_CLIENT_SECRET, AdoClientSecret
            .Destructure.ByTransforming<CreateAiConnectionRequest>(request => new
            {
                request.DisplayName,
                request.EndpointUrl,
                request.Models,
                ApiKey = RedactSecret(request.ApiKey),
                request.ModelCapabilities,
                request.ModelCategory,
            })
            .Destructure.ByTransforming<UpdateAiConnectionRequest>(request => new
            {
                request.DisplayName,
                request.EndpointUrl,
                request.Models,
                ApiKey = RedactSecret(request.ApiKey),
                request.ModelCapabilities,
            })
            .Destructure.ByTransforming<CreateClientProviderConnectionRequest>(request => new
            {
                request.ProviderFamily,
                request.HostBaseUrl,
                request.AuthenticationKind,
                request.UserName,
                request.OAuthTenantId,
                request.OAuthClientId,
                request.DisplayName,
                Secret = RedactSecret(request.Secret),
                request.IsActive,
            })
            .Destructure.ByTransforming<PatchClientProviderConnectionRequest>(request => new
            {
                request.HostBaseUrl,
                request.AuthenticationKind,
                request.UserName,
                request.OAuthTenantId,
                request.OAuthClientId,
                request.DisplayName,
                Secret = RedactSecret(request.Secret),
                request.IsActive,
            })
            .Destructure.ByTransforming<DiscoverModelsRequest>(request => new
            {
                request.EndpointUrl,
                ApiKey = RedactSecret(request.ApiKey),
            })
            .Destructure.ByTransforming<HttpRequest>(r => new
            {
                r.Method,
                r.Path,
                HasProCursorSharedKey = r.Headers.ContainsKey(ProCursorSharedKeyAuthenticationDefaults.HeaderName),
            });

        if (!context.HostingEnvironment.IsDevelopment())
        {
            configuration.WriteTo.Console(new JsonFormatter());
        }

        var lokiUrl = context.Configuration["LOKI_URL"];
        if (!string.IsNullOrWhiteSpace(lokiUrl))
        {
            configuration.WriteTo.GrafanaLoki(
                lokiUrl,
                [new LokiLabel { Key = "app", Value = "meister-dev-propr" }]);
        }
    });

    builder.Services.Configure<HostOptions>(opts =>
        opts.ShutdownTimeout = TimeSpan.FromMinutes(3));

    builder.Services.AddInfrastructureSupport(builder.Configuration, builder.Environment);
    builder.Services.AddReviewingModule(
        builder.Configuration,
        builder.Environment,
        GetSelectedCommentRelevanceFilterId());
    builder.Services.AddReviewWorkspaceServices(builder.Configuration);
    builder.Services.AddCrawlingModule(builder.Configuration, builder.Environment);
    builder.Services.AddClientsModule(builder.Configuration, builder.Environment);
    builder.Services.AddIdentityAndAccessModule(builder.Configuration, builder.Environment);
    builder.Services.AddMentionsModule(builder.Configuration, builder.Environment);
    builder.Services.AddPromptCustomizationModule(builder.Configuration, builder.Environment);
    builder.Services.AddUsageReportingModule(builder.Configuration, builder.Environment);
    builder.Services.AddLicensingModule(builder.Configuration, builder.Environment);
    builder.Services.AddReviewArchiveModule(builder.Configuration, builder.Environment);
    builder.Services.AddProCursorRemoteMode(builder.Configuration);
    builder.Services.AddScoped<ProCursorRuntimeConfigurationProjectionService>();
    builder.Services.AddScoped<ManagedRemoteProCursorGateway>();
    builder.Services.AddScoped<LocalProPrScmBroker>();
    builder.Services.AddScoped<LocalProPrEmbeddingBroker>();
    if (hasDatabaseConnectionString)
    {
        builder.Services.AddScoped<IProCursorKnowledgeSourceRepository, ProCursorKnowledgeSourceRepository>();
    }

    builder.Services.AddAuthentication(ProCursorSharedKeyAuthenticationDefaults.Scheme)
        .AddScheme<AuthenticationSchemeOptions, ProCursorSharedKeyAuthenticationHandler>(
            ProCursorSharedKeyAuthenticationDefaults.Scheme,
            _ => { });
    builder.Services.AddAuthorization();

    if (hasDatabaseConnectionString)
    {
        builder.Services.AddFeatureManagement()
            .UseDisabledFeaturesHandler(new PremiumFeatureDisabledHandler());
    }

    // Data protection: used to encrypt sensitive configuration values (e.g., AdoClientSecret).
    var dataProtectionBuilder = builder.Services.AddDataProtection()
        .SetApplicationName("MeisterProPR");
    var dataProtectionKeysPath = builder.Configuration["MEISTER_DATA_PROTECTION_KEYS_PATH"];
    if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    {
        Directory.CreateDirectory(dataProtectionKeysPath);
        dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
    }

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        // Trust loopback and private proxy networks by default; deployments with public ingress should
        // extend this list explicitly instead of opening forwarded headers to every remote address.
        options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Loopback, 8));
        options.KnownIPNetworks.Add(new IPNetwork(IPAddress.IPv6Loopback, 128));
        options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
        options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    });

    builder.Services.AddSingleton<IValidator<CreateClientRequest>, CreateClientRequestValidator>();
    builder.Services.AddSingleton<IValidator<PatchClientRequest>, PatchClientRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<CreateClientProviderConnectionRequest>,
            CreateClientProviderConnectionRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<PatchClientProviderConnectionRequest>,
            PatchClientProviderConnectionRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<CreateClientProviderScopeRequest>, CreateClientProviderScopeRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<PatchClientProviderScopeRequest>, PatchClientProviderScopeRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<SetClientReviewerIdentityRequest>, SetClientReviewerIdentityRequestValidator>();
    builder.Services.AddSingleton<IValidator<CreateTenantRequest>, CreateTenantRequestValidator>();
    builder.Services.AddSingleton<IValidator<UpdateTenantRequest>, UpdateTenantRequestValidator>();
    builder.Services.AddSingleton<IValidator<UpdateTenantMembershipRequest>, UpdateTenantMembershipRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<CreateTenantSsoProviderRequest>, CreateTenantSsoProviderRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<UpdateTenantSsoProviderRequest>, UpdateTenantSsoProviderRequestValidator>();
    builder.Services.AddSingleton<IValidator<CreateAdminCrawlConfigRequest>, CreateAdminCrawlConfigRequestValidator>();
    builder.Services.AddSingleton<IValidator<PatchAdminCrawlConfigRequest>, PatchAdminCrawlConfigRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<CreateAdminWebhookConfigRequest>, CreateAdminWebhookConfigRequestValidator>();
    builder.Services
        .AddSingleton<IValidator<PatchAdminWebhookConfigRequest>, PatchAdminWebhookConfigRequestValidator>();

    // Register ReviewJobWorker as singleton so WorkerHealthCheck can inject it by concrete type,
    // then forward the same instance as IHostedService.
    builder.Services.AddSingleton<ReviewJobWorker>();
    if (!disableHostedServices)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReviewJobWorker>());
    }

    // AdoPrCrawlerWorker needs persistent crawl-configuration storage.
    // Register it unconditionally for DI/health consumers, but do not start it in test hosts
    // or in hosts that explicitly suppress background workers.
    builder.Services.AddSingleton<AdoPrCrawlerWorker>();
    if (!isTesting && !disableHostedServices)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AdoPrCrawlerWorker>());
    }

    var proCursorGatewayMode = GetEffectiveProCursorMode(builder.Configuration);
    var useRemoteProCursor = IsRemoteProCursorMode(proCursorGatewayMode);

    builder.Services.AddScoped<IProCursorGateway>(sp =>
        useRemoteProCursor
            ? sp.GetRequiredService<ManagedRemoteProCursorGateway>()
            : sp.GetRequiredService<DisabledProCursorGateway>());

    builder.Services.RemoveAll<IProCursorScmBroker>();
    builder.Services.RemoveAll<IProCursorEmbeddingBroker>();
    builder.Services.AddScoped<IProCursorScmBroker>(sp => sp.GetRequiredService<LocalProPrScmBroker>());
    builder.Services.AddScoped<IProCursorEmbeddingBroker>(sp => sp.GetRequiredService<LocalProPrEmbeddingBroker>());

    // MentionScanWorker (producer) and MentionReplyWorker (consumer) share a single bounded
    // Channel<MentionReplyJob>. Channel capacity is 1000; writer blocks when full (Wait mode).
    var mentionChannel = Channel.CreateBounded<MentionReplyJob>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    builder.Services.AddSingleton(mentionChannel);
    builder.Services.AddSingleton(mentionChannel.Reader);
    builder.Services.AddSingleton(mentionChannel.Writer);

    builder.Services.AddSingleton<MentionScanWorker>();
    if (!isTesting && !disableHostedServices)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MentionScanWorker>());
    }

    builder.Services.AddSingleton<MentionReplyWorker>();
    if (!isTesting && !disableHostedServices)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MentionReplyWorker>());
    }

    // RetentionPurgeWorker deletes elapsed retained raw PR data. It depends on the review-archive
    // store, which is only registered when a database connection string is configured, so gate it
    // the same way to keep it inert in DB-less test runs.
    builder.Services.AddSingleton<RetentionPurgeWorker>();
    if (hasDatabaseConnectionString && !isTesting && !disableHostedServices)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionPurgeWorker>());
    }

    var allowedOrigins = BrowserOriginPolicy.GetAllowedOrigins(builder.Configuration);

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy
                .WithOrigins(allowedOrigins)
                // *.visualstudio.com cannot be expressed as a static origin string;
                // use a predicate so any subdomain is matched.
                .SetIsOriginAllowed(origin => BrowserOriginPolicy.IsAllowedOrigin(origin, allowedOrigins))
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
            opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

    builder.Services.AddSingleton<ReviewJobMetrics>();

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("MeisterProPR.Api"))
        .WithTracing(tracing => tracing
            .AddSource(ReviewJobTelemetry.Source.Name)
            .AddSource("MeisterProPR.Webhooks")
            .AddSource("MeisterProPR.Crawling")
            .AddSource("MeisterProPR.Infrastructure")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o =>
            {
                var endpoint = builder.Configuration["OTLP_ENDPOINT"];
                if (!string.IsNullOrEmpty(endpoint))
                {
                    o.Endpoint = new Uri(endpoint);
                }
            }))
        .WithMetrics(metrics => metrics
            .AddMeter("MeisterProPR")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddPrometheusExporter());

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "Meister DEV ProPR API", Version = "v1" });
        foreach (var xmlFile in Directory.GetFiles(AppContext.BaseDirectory, "MeisterProPR.*.xml"))
        {
            options.IncludeXmlComments(xmlFile);
        }
    });

    var healthChecksBuilder = builder.Services.AddHealthChecks()
        .AddCheck<WorkerHealthCheck>("worker");

    if (useRemoteProCursor)
    {
        builder.Services.AddHttpClient(nameof(RemoteProCursorHealthCheck));
        healthChecksBuilder.AddCheck<RemoteProCursorHealthCheck>("procursor-remote");
    }

    if (hasDatabaseConnectionString)
    {
        healthChecksBuilder.AddCheck<DatabaseHealthCheck>("database");
    }

    static Task WriteHealthResponse(HttpContext httpContext, HealthReport report)
    {
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds,
                    data = entry.Value.Data.ToDictionary(item => item.Key, item => item.Value),
                }),
        };

        return httpContext.Response.WriteAsJsonAsync(payload);
    }

    var app = builder.Build();

    if (args.Contains("--generate-openapi", StringComparer.OrdinalIgnoreCase))
    {
        using var scope = app.Services.CreateScope();
        var swaggerProvider = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>();
        var openApi = swaggerProvider.GetSwagger("v1");
        var outputPath = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "openapi.json"));

        await using var stream = File.Create(outputPath);
        await using var writer = new StreamWriter(stream);
        var jsonWriter = new OpenApiJsonWriter(writer);
        openApi.SerializeAsV3(jsonWriter);
        await writer.FlushAsync();

        Log.Information("Generated OpenAPI document at {OpenApiPath}", outputPath);
        return;
    }

    // Apply migrations, secret backfill, and startup recovery when a database is configured.
    if (hasDatabaseConnectionString)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

        // Apply any pending migrations automatically on startup
        await db.Database.MigrateAsync();

        var systemTenantBootstrapService = scope.ServiceProvider.GetService<SystemTenantBootstrapService>();
        if (systemTenantBootstrapService is not null)
        {
            await systemTenantBootstrapService.SeedAsync();
        }

        var secretBackfillService = scope.ServiceProvider.GetRequiredService<SecretBackfillService>();
        await secretBackfillService.BackfillAsync();

        // Startup recovery: transition stale Processing jobs (e.g., from a crash) back to Pending
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var staleJobs = await jobRepo.GetProcessingJobsAsync();
        foreach (var job in staleJobs)
        {
            await jobRepo.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending);
            Log.Warning(
                "Startup recovery: job {JobId} for PR#{PrId} was stale (Processing); reset to Pending",
                job.Id,
                job.PullRequestId);
        }

        // Seed bootstrap admin user if none exists
        var bootstrapService = scope.ServiceProvider.GetService<AdminBootstrapService>();
        if (bootstrapService is not null)
        {
            await bootstrapService.SeedAsync();
        }
    }

    app.UseForwardedHeaders();

    app.UseSerilogRequestLogging(opts =>
    {
        opts.GetLevel = (httpContext, _, _) =>
            httpContext.Request.Path.StartsWithSegments("/healthz")
            || httpContext.Request.Path.StartsWithSegments("/livez")
                ? LogEventLevel.Verbose // Verbose = Trace in Serilog
                : LogEventLevel.Information;
    });

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Chrome Private Network Access: when a public origin calls localhost the browser
    // sends a preflight with Access-Control-Request-Private-Network: true and requires
    // Access-Control-Allow-Private-Network: true in the response before allowing the request.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        {
            ctx.Response.Headers.Append("Access-Control-Allow-Private-Network", "true");
        }

        await next();
    });

    app.UseCors();
    app.UseAuthentication();
    // Populate admin, client-role, and tenant-role auth context before controller dispatch.
    app.UseMiddleware<AuthMiddleware>();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks(
        "/livez",
        new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthResponse,
        });
    app.MapHealthChecks(
        "/healthz",
        new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponse,
        });
    app.MapPrometheusScrapingEndpoint();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException and not InvalidOperationException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point for the API application used by tests and host.</summary>
public partial class Program
{
    private static readonly HttpClient HealthCheckHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    // Not meant to be instantiated directly; WebApplicationFactory<Program> only uses this type
    // as a generic marker to locate the assembly, so it can't be made static.
    protected Program()
    {
    }

    internal static async Task<bool> TryRunHealthCheckAsync(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--healthcheck", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var response = await HealthCheckHttpClient.GetAsync(args[1], HttpCompletionOption.ResponseHeadersRead);
            Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            Environment.ExitCode = 1;
        }

        return true;
    }

    internal static string GetEffectiveProCursorMode(IConfiguration configuration)
    {
        var mode = new ProCursorRemoteOptions
        {
            Mode = configuration["PROCURSOR_REMOTE_MODE"],
            ServiceBaseUrl = configuration["PROCURSOR_SERVICE_BASE_URL"],
            SharedKey = configuration["PROCURSOR_SHARED_KEY"],
        }.GetEffectiveMode();

        return mode switch
        {
            var value when IsRemoteProCursorMode(value) => ProCursorRemoteOptions.ProprManagedRemoteMode,
            var value when IsDisabledProCursorMode(value) => ProCursorRemoteOptions.DisabledMode,
            _ => ProCursorRemoteOptions.DisabledMode,
        };
    }

    internal static bool IsRemoteProCursorMode(string? mode)
    {
        return string.Equals(mode, ProCursorRemoteOptions.ProprManagedRemoteMode, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDisabledProCursorMode(string? mode)
    {
        return string.Equals(mode, ProCursorRemoteOptions.DisabledMode, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetSelectedCommentRelevanceFilterId()
    {
        return "hybrid-v1";
    }
}
