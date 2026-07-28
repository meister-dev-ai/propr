// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MeisterDev.ProPR.Observability;

/// <summary>Registers the OpenTelemetry pipelines shared by the ProPR hosts.</summary>
public static class ProPrTelemetryServiceCollectionExtensions
{
    /// <summary>Wires metrics always, and traces only when an export target is configured.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="serviceName">The resource service name reported to the backend.</param>
    /// <param name="traceSources">The activity source names this host emits domain spans from.</param>
    /// <param name="meters">The meter names this host records metrics under.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    ///     The metrics pipeline stays unconditional and unfiltered: it is a pull-based, pre-aggregated
    ///     view whose cost does not grow with request count, and it is what keeps outbound traffic
    ///     observable once the noisy trace spans are filtered away.
    /// </remarks>
    public static IServiceCollection AddProPrTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        IEnumerable<string> traceSources,
        IEnumerable<string> meters)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ProPrTelemetryOptions.FromConfiguration(configuration);
        services.AddSingleton(options);

        var meterNames = meters as ICollection<string> ?? [.. meters];

        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics =>
            {
                foreach (var meter in meterNames)
                {
                    metrics.AddMeter(meter);
                }

                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddPrometheusExporter();
            });

        if (!options.TracingEnabled)
        {
            return services;
        }

        var sourceNames = traceSources as ICollection<string> ?? [.. traceSources];

        builder.WithTracing(tracing =>
        {
            foreach (var source in sourceNames)
            {
                tracing.AddSource(source);
            }

            // Leaving the sampler unset at the default ratio keeps the SDK's own OTEL_TRACES_SAMPLER
            // and OTEL_TRACES_SAMPLER_ARG variables in charge for operators who prefer the spec knobs.
            if (options.TraceSampleRatio < 1.0d)
            {
                tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)));
            }

            tracing
                .AddAspNetCoreInstrumentation(instrumentation =>
                    instrumentation.Filter = context => options.ShouldTracePath(context.Request.Path.Value))
                .AddHttpClientInstrumentation(instrumentation =>
                    instrumentation.FilterHttpRequestMessage = request =>
                        options.ShouldTraceOutboundRequest(request.RequestUri, BackgroundActivityScope.IsActive))
                .AddOtlpExporter(exporter => exporter.Endpoint = options.OtlpEndpoint!);
        });

        return services;
    }
}
