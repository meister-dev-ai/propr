// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using FluentValidation;
using MeisterProPR.Api.Controllers;
using MeisterProPR.Api.HealthChecks;
using MeisterProPR.Api.Middleware;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Api.Validators;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Clients;
using MeisterProPR.Infrastructure.Features.Crawling;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.Mentions;
using MeisterProPR.Infrastructure.Features.PromptCustomization;
using MeisterProPR.Infrastructure.Features.Reviewing;
using MeisterProPR.Infrastructure.Features.UsageReporting;
using MeisterProPR.ProCursor.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.Grafana.Loki;
using Swashbuckle.AspNetCore.Swagger;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Ensure user-secrets are part of the application's IConfiguration in Development
    // so values can come from env vars, user secrets, or appsettings.
    if (builder.Environment.IsDevelopment())
    {
        builder.Configuration.AddUserSecrets<Program>(true);
    }

    var hasDatabaseConnectionString = builder.Configuration.HasDatabaseConnectionString();
    var isTesting = builder.Environment.IsEnvironment("Testing");

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        static string? RedactSecret(string? value)
            => string.IsNullOrWhiteSpace(value) ? value : "[REDACTED]";

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
            .Destructure.ByTransforming<SetAdoCredentialsRequest>(request => new
            {
                request.TenantId,
                request.ClientId,
                Secret = RedactSecret(request.Secret),
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
                labels: [new LokiLabel { Key = "app", Value = "meister-dev-propr" }]);
        }
    });

    builder.Services.Configure<HostOptions>(opts =>
        opts.ShutdownTimeout = TimeSpan.FromMinutes(3));

    builder.Services.AddInfrastructureSupport(builder.Configuration, builder.Environment);
    builder.Services.AddReviewingModule(builder.Configuration, builder.Environment);
    builder.Services.AddCrawlingModule(builder.Configuration, builder.Environment);
    builder.Services.AddClientsModule(builder.Configuration, builder.Environment);
    builder.Services.AddIdentityAndAccessModule(builder.Configuration, builder.Environment);
    builder.Services.AddMentionsModule(builder.Configuration, builder.Environment);
    builder.Services.AddPromptCustomizationModule(builder.Configuration, builder.Environment);
    builder.Services.AddUsageReportingModule(builder.Configuration, builder.Environment);
    builder.Services.AddProCursorModule(builder.Configuration, builder.Environment);

    // Data protection: used to encrypt sensitive configuration values (e.g., AdoClientSecret).
    var dataProtectionBuilder = builder.Services.AddDataProtection()
        .SetApplicationName("MeisterProPR");
    var dataProtectionKeysPath = builder.Configuration["MEISTER_DATA_PROTECTION_KEYS_PATH"];
    if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    {
        Directory.CreateDirectory(dataProtectionKeysPath);
        dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
    }
    builder.Services.AddSingleton<IValidator<CreateClientRequest>, CreateClientRequestValidator>();
    builder.Services.AddSingleton<IValidator<SetAdoCredentialsRequest>, SetAdoCredentialsRequestValidator>();
    builder.Services.AddSingleton<IValidator<SetReviewerIdentityRequest>, SetReviewerIdentityRequestValidator>();
    builder.Services.AddSingleton<IValidator<PatchClientRequest>, PatchClientRequestValidator>();
    builder.Services.AddSingleton<IValidator<CreateClientAdoOrganizationScopeRequest>, CreateClientAdoOrganizationScopeRequestValidator>();
    builder.Services.AddSingleton<IValidator<PatchClientAdoOrganizationScopeRequest>, PatchClientAdoOrganizationScopeRequestValidator>();
    builder.Services.AddSingleton<IValidator<CreateAdminCrawlConfigRequest>, CreateAdminCrawlConfigRequestValidator>();
    builder.Services.AddSingleton<IValidator<PatchAdminCrawlConfigRequest>, PatchAdminCrawlConfigRequestValidator>();

    // Register ReviewJobWorker as singleton so WorkerHealthCheck can inject it by concrete type,
    // then forward the same instance as IHostedService.
    builder.Services.AddSingleton<ReviewJobWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ReviewJobWorker>());

    // AdoPrCrawlerWorker needs persistent crawl-configuration storage.
    // Register it unconditionally for DI/health consumers, but do not start it in test hosts.
    builder.Services.AddSingleton<AdoPrCrawlerWorker>();
    if (!isTesting)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AdoPrCrawlerWorker>());
    }

    // ProCursor indexing uses a dedicated durable worker and exposes live state via health checks.
    // Keep the hosted-service registration active for DI/health/registration tests; the worker
    // tolerates incomplete ProCursor graphs in non-DB test hosts and idles when it cannot resolve them.
    builder.Services.AddSingleton<ProCursorIndexWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProCursorIndexWorker>());

    // ProCursor usage reporting uses a dedicated rollup worker so reads can rely on refreshed aggregates.
    builder.Services.AddSingleton<ProCursorTokenUsageRollupWorker>();
    if (!isTesting)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProCursorTokenUsageRollupWorker>());
    }

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
    if (!isTesting)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MentionScanWorker>());
    }

    builder.Services.AddSingleton<MentionReplyWorker>();
    if (!isTesting)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MentionReplyWorker>());
    }

    // Fixed origins: testbed (localhost:3000) and Azure DevOps.
    // Additional origins can be added via CORS_ORIGINS (comma-separated).
    var fixedOrigins = new[]
    {
        "http://localhost:3000",
        "https://localhost:3000",
        "https://dev.azure.com",
    };
    var extraOrigins = (builder.Configuration["CORS_ORIGINS"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var allowedOrigins = fixedOrigins.Concat(extraOrigins).ToArray();

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy
                .WithOrigins(allowedOrigins)
                // *.visualstudio.com cannot be expressed as a static origin string;
                // use a predicate so any subdomain is matched.
                .SetIsOriginAllowed(origin =>
                    allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) ||
                    Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                    (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) ||
                     uri.Host.EndsWith(".gallerycdn.vsassets.io", StringComparison.OrdinalIgnoreCase)))
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    builder.Services.AddControllers()
        .AddApplicationPart(typeof(ProCursorKnowledgeSourcesController).Assembly)
        .AddJsonOptions(opts =>
            opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

    builder.Services.AddSingleton<ReviewJobMetrics>();

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddSource(ReviewJobTelemetry.Source.Name)
            .AddSource("MeisterProPR.Infrastructure")
            .AddAspNetCoreInstrumentation()
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
            .AddPrometheusExporter());

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo { Title = "Meister DEV ProPR API", Version = "v1" });
        foreach (var xmlFile in Directory.GetFiles(AppContext.BaseDirectory, "MeisterProPR.*.xml"))
        {
            options.IncludeXmlComments(xmlFile);
        }
    });

    var healthChecksBuilder = builder.Services.AddHealthChecks()
        .AddCheck<WorkerHealthCheck>("worker")
        .AddCheck<ProCursorIndexWorkerHealthCheck>("procursor-index-worker")
        .AddCheck<ProCursorTokenUsageRollupWorkerHealthCheck>("procursor-token-usage-rollup-worker");

    if (hasDatabaseConnectionString)
    {
        healthChecksBuilder.AddCheck<DatabaseHealthCheck>("database");
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

        var secretBackfillService = scope.ServiceProvider.GetRequiredService<MeisterProPR.Infrastructure.Services.SecretBackfillService>();
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
        var bootstrapService = scope.ServiceProvider.GetService<MeisterProPR.Infrastructure.Auth.AdminBootstrapService>();
        if (bootstrapService is not null)
        {
            await bootstrapService.SeedAsync();
        }
    }

    app.UseSerilogRequestLogging(opts =>
    {
        opts.GetLevel = (httpContext, _, _) =>
            httpContext.Request.Path.StartsWithSegments("/healthz")
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
    app.UseMiddleware<AuthMiddleware>();
    app.MapControllers();
    app.MapHealthChecks("/healthz");
    app.MapPrometheusScrapingEndpoint();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException and not InvalidOperationException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Entry point for the API application used by tests and host.</summary>
public partial class Program
{
}
