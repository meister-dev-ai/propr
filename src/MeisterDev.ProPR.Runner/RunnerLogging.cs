// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Telemetry;
using Serilog;
using Serilog.Events;

namespace MeisterDev.ProPR.Runner;

/// <summary>
///     How the runner host logs.
/// </summary>
/// <remarks>
///     A named method, not a lambda in the host's startup, so a test can compose the logger and read what it
///     writes. The credential-scrubbing transforms are the part of the composition that has to keep working.
/// </remarks>
internal static class RunnerLogging
{
    /// <summary>Composes the runner's logger.</summary>
    /// <param name="settings">Where the operator's log level and display name are read from.</param>
    /// <param name="logger">The logger configuration to compose.</param>
    /// <param name="hostName">The machine the runner is on, used where no display name is configured.</param>
    public static LoggerConfiguration Configure(
        IConfiguration settings,
        LoggerConfiguration logger,
        string hostName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var level = ParseLevel(settings["RUNNER_LOG_LEVEL"]);

        // The credential-scrubbing transforms come first, before anything can be written. The runner relays every
        // model call and holds provider endpoints, so the destructured rendering of a connection or an endpoint
        // carries a credential on this side too. The policy is the one the API host applies, not a copy of it,
        // because a copy is what drifts.
        return AiConnectionLogRedaction.ApplyCredentialFallback(AiConnectionLogRedaction.Apply(logger))
            // Read from the environment rather than appsettings.json. The host had a settings file whose log
            // level nothing consumed, which is worse than no knob at all: an operator changes it and nothing
            // happens.
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            // The HTTP client factory writes four lines per request at Information, and an idle runner asks for
            // work every few seconds. At Information that traffic makes up most of the log and is charged per
            // line by most log backends. Held at Warning unless the operator has asked for Debug, where the
            // request trace is what is being read.
            .MinimumLevel.Override(
                "System.Net.Http.HttpClient",
                level <= LogEventLevel.Debug ? LogEventLevel.Debug : LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // The runner names itself in every line and every span. A trace that cannot tell runner work from
            // control-plane work is a trace that cannot answer where a review actually ran.
            .Enrich.WithProperty("service.name", RunnerHostIdentity.ServiceName)
            .Enrich.WithProperty("runner.display_name", settings["RUNNER_DISPLAY_NAME"] ?? hostName);
    }

    private static LogEventLevel ParseLevel(string? configured)
    {
        return Enum.TryParse<LogEventLevel>(configured, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Information;
    }
}
