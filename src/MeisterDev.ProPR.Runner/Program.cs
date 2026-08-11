// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Net.Http.Headers;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.CodeAnalysis.Roslyn.DependencyInjection;
using MeisterDev.ProPR.CodeAnalysis.TreeSitter.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.ProRV.DependencyInjection;
using MeisterDev.ProPR.Runner;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MeisterDev.ProPR.Observability;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

// The container's HEALTHCHECK runs this same DLL with --healthcheck. Handled before anything is built:
// otherwise the probe starts a second web host, fails to bind the port already in use, and reports an
// entirely healthy container as unhealthy.
if (await RunnerHealthProbe.TryRunAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    // Read from the environment rather than appsettings.json. The host had a settings file whose log
    // level nothing consumed, which is worse than no knob at all: an operator changes it and nothing
    // happens.
    .MinimumLevel.Is(ParseLogLevel(context.Configuration["RUNNER_LOG_LEVEL"]))
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    // The HTTP client factory writes four lines per request at Information, and an idle runner asks for work
    // every few seconds. At Information that traffic makes up most of the log and is charged per line by
    // most log backends. Held at Warning unless the operator has asked for Debug, where the request trace is
    // what is being read.
    .MinimumLevel.Override(
        "System.Net.Http.HttpClient",
        ParseLogLevel(context.Configuration["RUNNER_LOG_LEVEL"]) <= LogEventLevel.Debug
            ? LogEventLevel.Debug
            : LogEventLevel.Warning)
    .Enrich.FromLogContext()
    // The runner names itself in every line and every span. A trace that cannot tell runner work from
    // control-plane work is a trace that cannot answer where a review actually ran.
    .Enrich.WithProperty("service.name", RunnerHostIdentity.ServiceName)
    .Enrich.WithProperty("runner.display_name", context.Configuration["RUNNER_DISPLAY_NAME"] ?? Environment.MachineName)
    .WriteTo.Console());

builder.Services.AddOptions<RunnerHostOptions>()
    .Configure(options =>
    {
        var configuration = builder.Configuration;
        options.ControlPlaneUrl = configuration["RUNNER_CONTROL_PLANE_URL"] ?? string.Empty;
        options.Credential = configuration["RUNNER_CREDENTIAL"];
        options.RegistrationToken = configuration["RUNNER_REGISTRATION_TOKEN"];
        options.DisplayName = configuration["RUNNER_DISPLAY_NAME"] ?? Environment.MachineName;
        options.Tags = configuration["RUNNER_TAGS"] ?? string.Empty;
        options.WorkRootPath = configuration["RUNNER_WORK_ROOT"] ?? options.WorkRootPath;

        if (int.TryParse(configuration["RUNNER_CAPACITY"], out var capacity))
        {
            options.Capacity = capacity;
        }

        if (int.TryParse(configuration["RUNNER_POLL_INTERVAL_SECONDS"], out var pollInterval))
        {
            options.PollIntervalSeconds = pollInterval;
        }

        if (int.TryParse(configuration["RUNNER_MAX_BACKOFF_SECONDS"], out var maxBackoff))
        {
            options.MaxBackoffSeconds = maxBackoff;
        }
    })
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The runner's own service identity, so a trace can answer where a review actually ran. Named
// differently from the control plane on purpose: two processes sharing one service name make a
// distributed review indistinguishable from an in-process one after the fact.
//
// Read through the same telemetry options the control plane uses, so the documented OTLP_ENDPOINT means
// the same thing on both. An unset endpoint installs no exporter at all rather than building a pipeline
// that sends nowhere.
var telemetry = ProPrTelemetryOptions.FromConfiguration(builder.Configuration);
if (telemetry.TracingEnabled)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            RunnerHostIdentity.ServiceName,
            serviceVersion: RunnerHostIdentity.Version,
            serviceInstanceId: builder.Configuration["RUNNER_DISPLAY_NAME"] ?? Environment.MachineName))
        .WithTracing(tracing => tracing
            .AddSource(RunnerHostIdentity.ActivitySourceName)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(exporter => exporter.Endpoint = telemetry.OtlpEndpoint!));
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RunnerHealthState>();

// The credential lives in memory and changes: a host enrolls after its clients exist and renews without
// restarting. It is attached to every call through a handler rather than a header fixed at construction,
// which on a first start would fix the value as "no credential at all".
builder.Services.AddSingleton<RunnerCredentialStore>();
builder.Services.AddTransient<RunnerCredentialHandler>();

builder.Services.AddHttpClient<ControlPlaneClient>((serviceProvider, http) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RunnerHostOptions>>().Value;
    var controlPlane = new Uri(options.ControlPlaneUrl.TrimEnd('/') + "/");

    // The credential below is reusable and long-lived, so plain HTTP would hand it to anyone on the path.
    // Loopback is exempted because a developer running both halves on one machine has no TLS to offer and
    // no network to observe; everything else has to be HTTPS.
    if (!controlPlane.IsLoopback && !string.Equals(controlPlane.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "RUNNER_CONTROL_PLANE_URL must be an https URL. The runner credential is sent on every call, "
            + "and plain HTTP exposes it to anyone on the network path. Loopback addresses are exempt.");
    }

    http.BaseAddress = controlPlane;
    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(RunnerHostIdentity.ServiceName, RunnerHostIdentity.Version));
}).AddHttpMessageHandler<RunnerCredentialHandler>();

builder.Services.AddSingleton<MeisterDev.ProPR.Runner.Execution.WorkspaceFetcher>();

// The review options decide how the pipeline behaves, and are bound by the pipeline's own binder so this
// host reads the same variables into the same fields the control plane does. Two bindings would drift.
builder.Services.AddOptions<AiReviewOptions>()
    .Configure(options => AiReviewOptionsBinder.Bind(options, builder.Configuration));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiReviewOptions>>().Value);

// The workspace root the review's git worktrees live under is the runner's own work root: it holds one
// job's checkout at a time and is purged when that job ends.
builder.Services.AddOptions<ReviewWorkspaceOptions>()
    .Configure<IOptions<RunnerHostOptions>>((options, host) => options.RootPath = host.Value.WorkRootPath);

// The pieces of the pipeline that live behind their own module registrations, mirrored from the control
// plane's composition: the ProRV knowledge catalog, and both structural-analysis backends behind the one
// composite every consumer depends on. This is why the runner image is not small: it carries the
// analysis natives so reference and definition lookups run against the local worktrees.
builder.Services.AddProRV();
builder.Services.AddCodeAnalysisTreeSitter();
builder.Services.AddCodeAnalysisRoslyn();
builder.Services.AddSingleton<IStructuralCodeAnalyzer>(sp => new CompositeStructuralCodeAnalyzer(
    new[]
    {
        sp.GetRequiredKeyedService<IStructuralCodeAnalyzer>(CodeAnalysisServiceCollectionExtensions.BackendKey),
        sp.GetRequiredKeyedService<IStructuralCodeAnalyzer>(CodeAnalysisRoslynServiceCollectionExtensions.BackendKey),
    }));

// Everything a running review sends to the control plane (proxied tools, relayed completions, ingest
// batches and the findings) goes out on this one client, under the same credential and base path.
builder.Services.AddHttpClient(
    RunnerJobExecutor.ExecutionHttpClientName, (serviceProvider, http) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<RunnerHostOptions>>().Value;
        http.BaseAddress = new Uri(new Uri(options.ControlPlaneUrl.TrimEnd('/') + "/"), "runners/execution/");

        // A single model call can take minutes on a reasoning model, and the relay waits for the whole
        // completion. The default hundred seconds would abandon calls the control plane is still paying for.
        http.Timeout = TimeSpan.FromMinutes(15);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(RunnerHostIdentity.ServiceName, RunnerHostIdentity.Version));
    }).AddHttpMessageHandler<RunnerCredentialHandler>();

builder.Services.AddSingleton<IRunnerJobExecutor, RunnerJobExecutor>();
builder.Services.AddHostedService<RunnerWorkLoop>();

builder.Services.AddHealthChecks()
    .AddCheck<RunnerHealthCheck>("runner");

var app = builder.Build();

// Health and nothing else. This host has no API and no admin surface: it is a computation host, and every
// endpoint it does not have is one less endpoint to secure on a machine that holds a customer's code.
app.MapHealthChecks("/livez", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz");

await app.RunAsync();

/// <summary>Parses the configured log level, falling back to Information on anything unrecognised.</summary>
static LogEventLevel ParseLogLevel(string? configured)
{
    return Enum.TryParse<LogEventLevel>(configured, ignoreCase: true, out var level)
        ? level
        : LogEventLevel.Information;
}

/// <summary>
///     Answers the container's own health probe without starting a server.
/// </summary>
internal static class RunnerHealthProbe
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    ///     Runs the probe when the arguments ask for one, and reports whether it did. Returning true means
    ///     the caller must exit rather than continue starting a host.
    /// </summary>
    /// <param name="args">Process arguments.</param>
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--healthcheck", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var response = await Client.GetAsync(args[1], HttpCompletionOption.ResponseHeadersRead);
            Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
        }
#pragma warning disable CA1031 // Any failure to reach the endpoint is the answer the probe exists to give.
        catch (Exception)
#pragma warning restore CA1031
        {
            Environment.ExitCode = 1;
        }

        return true;
    }
}

/// <summary>How this host names itself in logs, traces, and to the control plane.</summary>
internal static class RunnerHostIdentity
{
    /// <summary>Service name, distinct from the control plane's so a trace can tell them apart.</summary>
    public const string ServiceName = "propr-runner";

    /// <summary>Host version, reported in the user agent.</summary>
    public static string Version =>
        typeof(RunnerHostIdentity).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Activity source the runner's own spans are emitted on.</summary>
    public const string ActivitySourceName = "MeisterDev.ProPR.Runner";
}

/// <summary>
///     Reports the loop's own view of itself. Only a starting host is ever unhealthy: a runner that cannot
///     reach its control plane is working correctly by retrying, and restarting it would replace a
///     diagnosable host with a crash loop over a problem at the other end of the network.
/// </summary>
internal sealed class RunnerHealthCheck(RunnerHealthState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var (current, detail) = state.Read();
        var data = new Dictionary<string, object> { ["state"] = current.ToString() };
        if (detail is not null)
        {
            data["detail"] = detail;
        }

        return Task.FromResult(
            current == RunnerHealthState.Status.Starting
                ? HealthCheckResult.Degraded("The runner has not yet asked for work.", data: data)
                : HealthCheckResult.Healthy(current.ToString(), data));
    }
}

/// <summary>Entry point marker, so the test host can reference this assembly.</summary>
public partial class Program;
