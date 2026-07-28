// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace MeisterDev.ProPR.Observability.Tests;

public sealed class ProPrTelemetryServiceCollectionExtensionsTests
{
    /// <summary>
    ///     With no collector configured there is nothing to export to, so no trace pipeline is built at
    ///     all rather than sampling and assembling spans that get thrown away.
    /// </summary>
    [Fact]
    public void AddProPrTelemetry_WithoutEndpoint_BuildsNoTracePipeline()
    {
        using var provider = Build();

        Assert.Null(provider.GetService<TracerProvider>());
    }

    [Fact]
    public void AddProPrTelemetry_WithEndpoint_BuildsTracePipeline()
    {
        using var provider = Build(("OTLP_ENDPOINT", "http://collector:4317"));

        Assert.NotNull(provider.GetService<TracerProvider>());
    }

    [Fact]
    public void AddProPrTelemetry_ExposesResolvedOptions()
    {
        using var provider = Build(("TELEMETRY_HTTP_CLIENT_TRACES", "off"));

        Assert.Equal(HttpClientTraceMode.Off, provider.GetRequiredService<ProPrTelemetryOptions>().HttpClientTraces);
    }

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new ServiceCollection()
            .AddProPrTelemetry(configuration, "test-service", ["TestSource"], ["TestMeter"])
            .BuildServiceProvider();
    }
}
