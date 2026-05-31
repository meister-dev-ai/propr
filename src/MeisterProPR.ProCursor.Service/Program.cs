// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.ProCursor.HealthChecks;
using MeisterProPR.ProCursor.Infrastructure.DependencyInjection;
using MeisterProPR.ProCursor.Infrastructure.Remote;
using MeisterProPR.ProCursor.Options;
using MeisterProPR.ProCursor.Persistence;
using MeisterProPR.ProCursor.Service.Auth;
using MeisterProPR.ProCursor.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.Grafana.Loki;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    if (await TryRunHealthCheckAsync(args))
    {
        return;
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddHttpContextAccessor();

    var hostOptions = new ProCursorHostOptions
    {
        ProPrBaseUrl = builder.Configuration["PROCURSOR_PROPR_BASE_URL"],
        SharedKey = builder.Configuration["PROCURSOR_SHARED_KEY"],
        RequestTimeoutSeconds = int.TryParse(builder.Configuration["PROCURSOR_REQUEST_TIMEOUT_SECONDS"], out var requestTimeoutSeconds)
            ? requestTimeoutSeconds
            : 30,
        RuntimeConfigurationTtlSeconds = int.TryParse(builder.Configuration["PROCURSOR_RUNTIME_CONFIG_TTL_SECONDS"], out var ttlSeconds)
            ? ttlSeconds
            : 300,
    };

    if (string.IsNullOrWhiteSpace(hostOptions.ProPrBaseUrl)
        || string.IsNullOrWhiteSpace(hostOptions.SharedKey)
        || string.IsNullOrWhiteSpace(builder.Configuration["PROCURSOR_DB_CONNECTION_STRING"]))
    {
        throw new InvalidOperationException(
            "PROCURSOR_PROPR_BASE_URL, PROCURSOR_SHARED_KEY, and PROCURSOR_DB_CONNECTION_STRING are required for the extracted ProCursor host.");
    }

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
            .Enrich.WithProperty("Application", "Meister DEV ProCursor")
            .Enrich.WithProperty("ServiceBoundary", "ProCursor")
            .Destructure.ByTransforming<HttpRequest>(request => new
            {
                request.Method,
                request.Path,
                HasProCursorSharedKey = request.Headers.ContainsKey(ProCursorSharedKeyAuthenticationDefaults.HeaderName),
            })
            .Destructure.ByTransforming<ProCursorHostOptions>(options => new
            {
                options.ProPrBaseUrl,
                SharedKey = RedactSecret(options.SharedKey),
            })
            ;

        if (!context.HostingEnvironment.IsDevelopment())
        {
            configuration.WriteTo.Console(new JsonFormatter());
        }

        var lokiUrl = context.Configuration["LOKI_URL"];
        if (!string.IsNullOrWhiteSpace(lokiUrl))
        {
            configuration.WriteTo.GrafanaLoki(
                lokiUrl,
                [new LokiLabel { Key = "app", Value = "meister-dev-procursor" }]);
        }
    });

    builder.Services.AddProCursorModule(builder.Configuration, builder.Environment);

    builder.Services.AddOptions<ProCursorHostOptions>()
        .Configure(options =>
        {
            options.ProPrBaseUrl = hostOptions.ProPrBaseUrl;
            options.SharedKey = hostOptions.SharedKey;
            options.RequestTimeoutSeconds = hostOptions.RequestTimeoutSeconds;
            options.RuntimeConfigurationTtlSeconds = hostOptions.RuntimeConfigurationTtlSeconds;
        })
        .ValidateDataAnnotations()
        .Validate(
            options =>
                !string.IsNullOrWhiteSpace(options.ProPrBaseUrl)
                && !string.IsNullOrWhiteSpace(options.SharedKey),
            "PROCURSOR_PROPR_BASE_URL and PROCURSOR_SHARED_KEY are required for the extracted ProCursor host.")
        .ValidateOnStart();

    builder.Services.AddHostedService<ProCursorRuntimeConfigurationWarmupService>();

    var dataProtectionBuilder = builder.Services.AddDataProtection()
        .SetApplicationName("MeisterProPR");
    var dataProtectionKeysPath = builder.Configuration["MEISTER_DATA_PROTECTION_KEYS_PATH"];
    if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    {
        Directory.CreateDirectory(dataProtectionKeysPath);
        dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
    }

    builder.Services.AddAuthentication(ProCursorSharedKeyAuthenticationDefaults.Scheme)
        .AddScheme<AuthenticationSchemeOptions, ProCursorSharedKeyAuthenticationHandler>(
            ProCursorSharedKeyAuthenticationDefaults.Scheme,
            _ => { });
    builder.Services.AddAuthorization();

    builder.Services.AddSingleton<ProCursorIndexWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProCursorIndexWorker>());
    builder.Services.AddSingleton<ProCursorTokenUsageRollupWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProCursorTokenUsageRollupWorker>());

    builder.Services.AddControllers();
    builder.Services.AddHealthChecks()
        .AddCheck<ProCursorIndexWorkerHealthCheck>("procursor-index-worker")
        .AddCheck<ProCursorTokenUsageRollupWorkerHealthCheck>("procursor-token-usage-rollup-worker");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("MeisterProPR.ProCursor.Service"))
        .WithTracing(tracing => tracing
            .AddSource("MeisterProPR.Infrastructure")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                var endpoint = builder.Configuration["OTLP_ENDPOINT"];
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    options.Endpoint = new Uri(endpoint);
                }
            }))
        .WithMetrics(metrics => metrics
            .AddMeter("MeisterProPR")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddPrometheusExporter());

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        await ApplyOperationalMigrationsAsync(db);
    }

    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (httpContext, _, _) =>
            httpContext.Request.Path.StartsWithSegments("/healthz")
            || httpContext.Request.Path.StartsWithSegments("/livez")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
    });

    static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        return context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString(),
                entries = report.Entries.ToDictionary(
                    item => item.Key,
                    item => new
                    {
                        status = item.Value.Status.ToString(),
                        description = item.Value.Description,
                    }),
            });
    }

    app.UseAuthentication();
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

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ProCursor service terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
///     Composition root for the extracted ProCursor service host.
/// </summary>
public partial class Program
{
    private static readonly HttpClient HealthCheckHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

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

    internal static Task ApplyOperationalMigrationsAsync(ProCursorOperationalDbContext db)
    {
        return db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true
            ? Task.CompletedTask
            : db.Database.MigrateAsync();
    }
}
