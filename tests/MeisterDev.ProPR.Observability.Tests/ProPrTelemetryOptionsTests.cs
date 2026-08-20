// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Observability;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Observability.Tests;

public sealed class ProPrTelemetryOptionsTests
{
    [Fact]
    public void FromConfiguration_WithoutSettings_DisablesTracingAndSuppressesBackgroundOutboundSpans()
    {
        var options = Build();

        Assert.False(options.TracingEnabled);
        Assert.Null(options.OtlpEndpoint);
        Assert.Equal(HttpClientTraceMode.Foreground, options.HttpClientTraces);
        Assert.Equal(1.0d, options.TraceSampleRatio);
        Assert.Equal(["/healthz", "/livez", "/metrics"], options.IgnoredPaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    public void FromConfiguration_WithUnusableEndpoint_LeavesTracingOff(string endpoint)
    {
        var options = Build(("OTLP_ENDPOINT", endpoint));

        Assert.False(options.TracingEnabled);
    }

    [Fact]
    public void FromConfiguration_WithEndpoint_EnablesTracing()
    {
        var options = Build(("OTLP_ENDPOINT", "http://collector:4317"));

        Assert.True(options.TracingEnabled);
        Assert.Equal(new Uri("http://collector:4317"), options.OtlpEndpoint);
    }

    /// <summary>
    ///     Managed OpenTelemetry agents inject the endpoint under the name the OpenTelemetry specification
    ///     gives it. While only the private name was read, the endpoint resolved to null in Azure, no trace
    ///     pipeline was built, and no request or dependency telemetry was exported. The setting was present,
    ///     so the configuration gave no indication of it.
    /// </summary>
    [Fact]
    public void FromConfiguration_WithTheStandardEndpointVariable_EnablesTracing()
    {
        var options = Build(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://agent:4317"));

        Assert.True(options.TracingEnabled);
        Assert.Equal(new Uri("http://agent:4317"), options.OtlpEndpoint);
    }

    /// <summary>
    ///     The API declares <c>OTLP_ENDPOINT</c> with an empty value in appsettings.json, so the key is
    ///     present whether or not an operator set it. A host supplying only the standard variable has to
    ///     resolve that one.
    /// </summary>
    [Fact]
    public void FromConfiguration_WithTheLegacyVariableDeclaredEmpty_StillReadsTheStandardOne()
    {
        var options = Build(
            ("OTLP_ENDPOINT", string.Empty),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://agent:4317"));

        Assert.True(options.TracingEnabled);
        Assert.Equal(new Uri("http://agent:4317"), options.OtlpEndpoint);
    }

    [Fact]
    public void FromConfiguration_WithBothEndpointVariables_PrefersTheExplicitOne()
    {
        var options = Build(
            ("OTLP_ENDPOINT", "http://explicit:4317"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://agent:4317"));

        Assert.Equal(new Uri("http://explicit:4317"), options.OtlpEndpoint);
    }

    [Theory]
    [InlineData("off", HttpClientTraceMode.Off)]
    [InlineData("OFF", HttpClientTraceMode.Off)]
    [InlineData("all", HttpClientTraceMode.All)]
    [InlineData("foreground", HttpClientTraceMode.Foreground)]
    [InlineData("nonsense", HttpClientTraceMode.Foreground)]
    public void FromConfiguration_ParsesHttpClientTraceMode_FallingBackToForeground(
        string configured,
        HttpClientTraceMode expected)
    {
        Assert.Equal(expected, Build(("TELEMETRY_HTTP_CLIENT_TRACES", configured)).HttpClientTraces);
    }

    [Theory]
    [InlineData("0.05", 0.05d)]
    [InlineData("1", 1.0d)]
    [InlineData("0", 0.0d)]
    [InlineData("-3", 0.0d)]
    [InlineData("42", 1.0d)]
    [InlineData("garbage", 1.0d)]
    public void FromConfiguration_ClampsSampleRatioIntoRange(string configured, double expected)
    {
        Assert.Equal(expected, Build(("TELEMETRY_TRACE_SAMPLE_RATIO", configured)).TraceSampleRatio);
    }

    /// <summary>Operators should not have to remember the leading slash.</summary>
    [Fact]
    public void FromConfiguration_NormalizesConfiguredIgnoredPaths()
    {
        var options = Build(("TELEMETRY_TRACE_IGNORED_PATHS", "healthz, /ready ,,alive"));

        Assert.Equal(["/healthz", "/ready", "/alive"], options.IgnoredPaths);
    }

    [Theory]
    [InlineData("/healthz", false)]
    [InlineData("/healthz/ready", false)]
    [InlineData("/HEALTHZ", false)]
    [InlineData("/metrics", false)]
    [InlineData("/api/clients", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void ShouldTracePath_SkipsProbeAndScrapeEndpoints(string? path, bool expected)
    {
        Assert.Equal(expected, Build().ShouldTracePath(path));
    }

    [Fact]
    public void ShouldTraceOutboundRequest_InForegroundMode_DropsBackgroundWorkButKeepsForegroundWork()
    {
        var options = Build();
        var target = new Uri("https://dev.azure.com/org/_apis/git/pullrequests");

        Assert.True(options.ShouldTraceOutboundRequest(target, isBackgroundWork: false));
        Assert.False(options.ShouldTraceOutboundRequest(target, isBackgroundWork: true));
    }

    /// <summary>A probe made outside a background scope is still noise, so the path check backs the scope up.</summary>
    [Fact]
    public void ShouldTraceOutboundRequest_InForegroundMode_DropsProbeTargetsRegardlessOfScope()
    {
        var options = Build();

        Assert.False(
            options.ShouldTraceOutboundRequest(
                new Uri("http://procursor:8081/healthz"),
                isBackgroundWork: false));
    }

    [Fact]
    public void ShouldTraceOutboundRequest_InAllMode_KeepsEverything()
    {
        var options = Build(("TELEMETRY_HTTP_CLIENT_TRACES", "all"));

        Assert.True(options.ShouldTraceOutboundRequest(new Uri("http://procursor:8081/healthz"), true));
        Assert.True(options.ShouldTraceOutboundRequest(null, true));
    }

    [Fact]
    public void ShouldTraceOutboundRequest_InOffMode_KeepsNothing()
    {
        var options = Build(("TELEMETRY_HTTP_CLIENT_TRACES", "off"));

        Assert.False(options.ShouldTraceOutboundRequest(new Uri("https://api.openai.com/v1/messages"), false));
    }

    private static ProPrTelemetryOptions Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return ProPrTelemetryOptions.FromConfiguration(configuration);
    }
}
