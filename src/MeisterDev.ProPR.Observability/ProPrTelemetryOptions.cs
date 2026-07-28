// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Observability;

/// <summary>Trace-volume settings shared by every ProPR host.</summary>
public sealed class ProPrTelemetryOptions
{
    /// <summary>Paths excluded from tracing when no explicit list is configured.</summary>
    private static readonly string[] DefaultIgnoredPaths = ["/healthz", "/livez", "/metrics"];

    /// <summary>Gets the OTLP endpoint traces are exported to, or <see langword="null" /> when tracing is off.</summary>
    public Uri? OtlpEndpoint { get; private init; }

    /// <summary>Gets a value indicating whether a trace pipeline should be built at all.</summary>
    /// <remarks>
    ///     Without a configured endpoint there is nobody to export to, so the host skips the trace
    ///     pipeline entirely instead of sampling and building spans that are then dropped on the floor.
    /// </remarks>
    public bool TracingEnabled => this.OtlpEndpoint is not null;

    /// <summary>Gets how much of the outbound HTTP traffic becomes trace spans.</summary>
    public HttpClientTraceMode HttpClientTraces { get; private init; } = HttpClientTraceMode.Foreground;

    /// <summary>Gets the head-sampling ratio applied to traces, between 0 and 1 inclusive.</summary>
    /// <remarks>
    ///     At the default of 1 no sampler is installed, which leaves the SDK's own
    ///     <c>OTEL_TRACES_SAMPLER</c> and <c>OTEL_TRACES_SAMPLER_ARG</c> variables in charge.
    /// </remarks>
    public double TraceSampleRatio { get; private init; } = 1.0d;

    /// <summary>Gets the request path prefixes that are never traced, inbound or outbound.</summary>
    public IReadOnlyList<string> IgnoredPaths { get; private init; } = DefaultIgnoredPaths;

    /// <summary>Reads the settings from configuration, falling back to the shipped defaults.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The resolved settings.</returns>
    public static ProPrTelemetryOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new ProPrTelemetryOptions
        {
            OtlpEndpoint = ParseEndpoint(configuration["OTLP_ENDPOINT"]),
            HttpClientTraces = ParseHttpClientTraceMode(configuration["TELEMETRY_HTTP_CLIENT_TRACES"]),
            TraceSampleRatio = ParseSampleRatio(configuration["TELEMETRY_TRACE_SAMPLE_RATIO"]),
            IgnoredPaths = ParseIgnoredPaths(configuration["TELEMETRY_TRACE_IGNORED_PATHS"]),
        };
    }

    /// <summary>Decides whether an inbound request path is worth tracing.</summary>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true" /> when the request should produce a span.</returns>
    public bool ShouldTracePath(string? path)
    {
        return !this.IsIgnoredPath(path);
    }

    /// <summary>Decides whether an outbound request is worth tracing.</summary>
    /// <param name="requestUri">The outbound request target, when known.</param>
    /// <param name="isBackgroundWork">Whether the caller is inside a <see cref="BackgroundActivityScope" />.</param>
    /// <returns><see langword="true" /> when the request should produce a span.</returns>
    public bool ShouldTraceOutboundRequest(Uri? requestUri, bool isBackgroundWork)
    {
        return this.HttpClientTraces switch
        {
            HttpClientTraceMode.Off => false,
            HttpClientTraceMode.All => true,
            _ => !isBackgroundWork && !this.IsIgnoredPath(requestUri?.AbsolutePath),
        };
    }

    private bool IsIgnoredPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var ignored in this.IgnoredPaths)
        {
            if (path.StartsWith(ignored, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Uri? ParseEndpoint(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint)
            ? endpoint
            : null;
    }

    private static HttpClientTraceMode ParseHttpClientTraceMode(string? value)
    {
        return Enum.TryParse<HttpClientTraceMode>(value, ignoreCase: true, out var mode)
            ? mode
            : HttpClientTraceMode.Foreground;
    }

    private static double ParseSampleRatio(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
        {
            return 1.0d;
        }

        return Math.Clamp(ratio, 0.0d, 1.0d);
    }

    private static IReadOnlyList<string> ParseIgnoredPaths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultIgnoredPaths;
        }

        var paths = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.StartsWith('/') ? path : "/" + path)
            .ToArray();

        return paths.Length == 0 ? DefaultIgnoredPaths : paths;
    }
}
