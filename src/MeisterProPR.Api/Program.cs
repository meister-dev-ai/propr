using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using MeisterProPR.Api.HealthChecks;
using MeisterProPR.Api.Middleware;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

static void RequireConfig(IConfiguration config, string name)
{
    if (string.IsNullOrWhiteSpace(config[name]))
    {
        throw new InvalidOperationException($"{name} is not set in configuration.");
    }
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Ensure user-secrets are part of the application's IConfiguration in Development
    // so values can come from env vars, user secrets, or appsettings.
    if (builder.Environment.IsDevelopment())
    {
        builder.Configuration.AddUserSecrets<Program>(true);
    }

    var dbConnectionString = builder.Configuration["DB_CONNECTION_STRING"];
    var isDbMode = !string.IsNullOrWhiteSpace(dbConnectionString);

    // Validation runs after CreateBuilder so builder.Configuration includes all
    // sources (env vars, user secrets, appsettings) and WebApplicationFactory
    // can inject overrides via UseSetting before these checks run.
    // InvalidOperationException is excluded from the catch filter below so these
    // failures propagate to callers such as WebApplicationFactory in tests.
    RequireConfig(builder.Configuration, "AI_ENDPOINT");
    RequireConfig(builder.Configuration, "AI_DEPLOYMENT");

    // MEISTER_CLIENT_KEYS is only required in legacy (non-DB) mode.
    // In DB mode it is optional and used solely for one-time bootstrap seeding.
    if (!isDbMode)
    {
        RequireConfig(builder.Configuration, "MEISTER_CLIENT_KEYS");
    }

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "MeisterProPR")
            // Scrub secrets from log output: X-Client-Key, X-Ado-Token, AZURE_CLIENT_SECRET, AdoClientSecret
            .Destructure.ByTransforming<HttpRequest>(r => new
            {
                r.Method,
                r.Path,
            });

        if (!context.HostingEnvironment.IsDevelopment())
        {
            configuration.WriteTo.Console(new JsonFormatter());
        }
    });

    builder.Services.Configure<HostOptions>(opts =>
        opts.ShutdownTimeout = TimeSpan.FromMinutes(3));

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddTransient<ReviewOrchestrationService>();

    // Only registered in DB mode — PrCrawlService depends on ICrawlConfigurationRepository
    // which is only available when DB_CONNECTION_STRING is configured.
    if (isDbMode)
    {
        builder.Services.AddScoped<IPrCrawlService, PrCrawlService>();
        builder.Services.AddScoped<IMentionScanService, MentionScanService>();
        builder.Services.AddScoped<IMentionReplyService, MentionReplyService>();
    }

    // Register ReviewJobWorker as singleton so WorkerHealthCheck can inject it by concrete type,
    // then forward the same instance as IHostedService.
    builder.Services.AddSingleton<ReviewJobWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ReviewJobWorker>());

    // AdoPrCrawlerWorker is only useful in DB mode (needs ICrawlConfigurationRepository).
    // Register it unconditionally — it will gracefully handle missing DI dependencies.
    builder.Services.AddSingleton<AdoPrCrawlerWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AdoPrCrawlerWorker>());

    // MentionScanWorker (producer) and MentionReplyWorker (consumer) share a single bounded
    // Channel<MentionReplyJob>. Channel capacity is 1000; writer blocks when full (Wait mode).
    // Both workers only run in DB mode — without DB there is no persistent scan state.
    if (isDbMode)
    {
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
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MentionScanWorker>());

        builder.Services.AddSingleton<MentionReplyWorker>();
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
        foreach (var xmlFile in Directory.GetFiles(AppContext.BaseDirectory, "MeisterProPR.*.xml"))
        {
            options.IncludeXmlComments(xmlFile);
        }
    });

    var healthChecksBuilder = builder.Services.AddHealthChecks()
        .AddCheck<WorkerHealthCheck>("worker");

    if (isDbMode)
    {
        healthChecksBuilder.AddCheck<DatabaseHealthCheck>("database");
    }

    var app = builder.Build();

    // DB migration + bootstrap + startup recovery (DB mode only)
    if (isDbMode)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

        // Apply any pending migrations automatically on startup
        await db.Database.MigrateAsync();

        // One-time bootstrap: seed clients from MEISTER_CLIENT_KEYS if clients table is empty
        var clientCount = await db.Clients.CountAsync();
        var legacyKeys = builder.Configuration["MEISTER_CLIENT_KEYS"];
        if (clientCount == 0 && !string.IsNullOrWhiteSpace(legacyKeys))
        {
            var keys = legacyKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var key in keys)
            {
                db.Clients.Add(
                    new ClientRecord
                    {
                        Id = Guid.NewGuid(),
                        Key = key,
                        DisplayName = "Bootstrapped from MEISTER_CLIENT_KEYS",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
            }

            await db.SaveChangesAsync();
            Log.Warning(
                "Bootstrap: seeded {Count} client(s) from MEISTER_CLIENT_KEYS. " +
                "This env var is deprecated in DB mode and will be ignored on subsequent startups.",
                keys.Length);
        }

        // Startup recovery: transition stale Processing jobs (e.g., from a crash) back to Pending
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var staleJobs = await jobRepo.GetProcessingJobsAsync();
        foreach (var job in staleJobs)
        {
            jobRepo.TryTransition(job.Id, JobStatus.Processing, JobStatus.Pending);
            Log.Warning(
                "Startup recovery: job {JobId} for PR#{PrId} was stale (Processing); reset to Pending.",
                job.Id,
                job.PullRequestId);
        }
    }

    app.UseSerilogRequestLogging();

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
    app.UseMiddleware<AdminKeyMiddleware>();
    app.UseMiddleware<ClientKeyMiddleware>();
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
