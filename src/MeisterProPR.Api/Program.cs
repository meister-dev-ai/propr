using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Api.HealthChecks;
using MeisterProPR.Api.Middleware;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.DependencyInjection;
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

    // ── Validate required config keys ─────────────────────────────────────────
    // Validation runs after CreateBuilder so builder.Configuration includes all
    // sources (env vars, user secrets, appsettings) and WebApplicationFactory
    // can inject overrides via UseSetting before these checks run.
    // InvalidOperationException is excluded from the catch filter below so these
    // failures propagate to callers such as WebApplicationFactory in tests.
    RequireConfig(builder.Configuration, "AI_ENDPOINT");
    RequireConfig(builder.Configuration, "AI_DEPLOYMENT");
    RequireConfig(builder.Configuration, "MEISTER_CLIENT_KEYS");

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "MeisterProPR");

        if (!context.HostingEnvironment.IsDevelopment())
        {
            configuration.WriteTo.Console(new JsonFormatter());
        }
    });

    // ── Host options ──────────────────────────────────────────────────────────
    builder.Services.Configure<HostOptions>(opts =>
        opts.ShutdownTimeout = TimeSpan.FromMinutes(3));

    // ── Infrastructure ────────────────────────────────────────────────────────
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddTransient<ReviewOrchestrationService>();

    // ── Background worker ─────────────────────────────────────────────────────
    // Register as singleton so WorkerHealthCheck can inject it by concrete type,
    // then forward the same instance as IHostedService.
    builder.Services.AddSingleton<ReviewJobWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ReviewJobWorker>());

    // ── CORS ──────────────────────────────────────────────────────────────────
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
                    (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                     (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) ||
                      uri.Host.EndsWith(".gallerycdn.vsassets.io", StringComparison.OrdinalIgnoreCase))))
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    // ── MVC ───────────────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
            opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

    // ── OpenTelemetry ─────────────────────────────────────────────────────────
    builder.Services.AddSingleton<ReviewJobMetrics>();

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddSource(ReviewJobTelemetry.Source.Name)
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

    // ── Swashbuckle ───────────────────────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        foreach (var xmlFile in Directory.GetFiles(AppContext.BaseDirectory, "MeisterProPR.*.xml"))
        {
            options.IncludeXmlComments(xmlFile);
        }
    });

    // ── Health checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddCheck<WorkerHealthCheck>("worker");

    // ══════════════════════════════════════════════════════════════════════════
    var app = builder.Build();
    // ══════════════════════════════════════════════════════════════════════════

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