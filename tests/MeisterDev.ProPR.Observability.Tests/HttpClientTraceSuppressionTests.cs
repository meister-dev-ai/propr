// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterDev.ProPR.Observability;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MeisterDev.ProPR.Observability.Tests;

/// <summary>
///     Exercises the real instrumentation pipeline rather than the predicate alone, so the claim that a
///     background scope removes outbound spouts of spans is checked end to end.
/// </summary>
/// <remarks>
///     The requests deliberately target a closed loopback port: the instrumentation records an attempt
///     the same way whether or not it connects, which keeps the test deterministic and server-free.
/// </remarks>
public sealed class HttpClientTraceSuppressionTests
{
    private static readonly Uri UnreachableTarget = new("http://127.0.0.1:1/anything");

    [Fact]
    public async Task ForegroundRequest_IsTraced()
    {
        var spans = await CaptureSpansAsync(isBackgroundWork: false);

        Assert.Single(spans);
    }

    [Fact]
    public async Task BackgroundRequest_IsNotTraced()
    {
        var spans = await CaptureSpansAsync(isBackgroundWork: true);

        Assert.Empty(spans);
    }

    /// <summary>
    ///     Suppressing the span must not cost the aggregate view: the request still lands in the metrics
    ///     histogram, which is what keeps outbound traffic observable after the spans are filtered away.
    /// </summary>
    [Fact]
    public async Task BackgroundRequest_IsStillCountedByMetrics()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddHttpClientInstrumentation()
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        using var tracerProvider = BuildTracerProvider([]);

        using (BackgroundActivityScope.Begin())
        {
            await SendAsync();
        }

        meterProvider.ForceFlush();

        Assert.Contains(exportedMetrics, metric => metric.Name == "http.client.request.duration");
    }

    private static async Task<List<Activity>> CaptureSpansAsync(bool isBackgroundWork)
    {
        var exportedSpans = new List<Activity>();
        using var tracerProvider = BuildTracerProvider(exportedSpans);

        if (isBackgroundWork)
        {
            using (BackgroundActivityScope.Begin())
            {
                await SendAsync();
            }
        }
        else
        {
            await SendAsync();
        }

        tracerProvider.ForceFlush();
        return exportedSpans;
    }

    private static TracerProvider BuildTracerProvider(ICollection<Activity> exportedSpans)
    {
        var options = ProPrTelemetryOptions.FromConfiguration(new ConfigurationBuilder().Build());

        return Sdk.CreateTracerProviderBuilder()
            .AddHttpClientInstrumentation(instrumentation =>
                instrumentation.FilterHttpRequestMessage = request =>
                    options.ShouldTraceOutboundRequest(request.RequestUri, BackgroundActivityScope.IsActive))
            .AddInMemoryExporter(exportedSpans)
            .Build();
    }

    private static async Task SendAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(UnreachableTarget);
        }
        catch (HttpRequestException)
        {
            // The connection is expected to be refused; only the instrumentation's reaction matters here.
        }
    }
}
