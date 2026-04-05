// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.Metrics;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Telemetry;

/// <summary>Exposes metrics for review jobs (histograms, counters, etc.).</summary>
public sealed class ReviewJobMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Creates the metrics meter and instruments.</summary>
    /// <param name="scopeFactory">Factory used to resolve scoped repositories for observations.</param>
    public ReviewJobMetrics(IServiceScopeFactory scopeFactory)
    {
        this._meter = new Meter("MeisterProPR", "1.0.0");
        this.JobDuration = this._meter.CreateHistogram<double>(
            "review_job_duration_seconds",
            "s",
            "Duration of review job processing");
        this._meter.CreateObservableGauge(
            "review_job_queue_depth",
            () => ObserveQueueDepth(scopeFactory),
            "jobs",
            "Number of jobs currently waiting to be processed");
        this.CrawlPrsDiscovered = this._meter.CreateCounter<long>(
            "meisterpropr_crawl_prs_discovered_total",
            "prs",
            "Total number of open PRs where the service account is assigned as reviewer");
        this.CrawlDuration = this._meter.CreateHistogram<double>(
            "meisterpropr_crawl_duration_seconds",
            "s",
            "Duration of a complete crawl cycle across all active configurations");
    }

    private static int ObserveQueueDepth(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        return jobRepository.GetPendingJobs().Count;
    }

    /// <summary>Histogram measuring PR crawl cycle durations in seconds.</summary>
    public Histogram<double> CrawlDuration { get; }

    /// <summary>Counter tracking total number of PRs discovered by the crawler.</summary>
    public Counter<long> CrawlPrsDiscovered { get; }

    /// <summary>Histogram measuring review job durations in seconds.</summary>
    public Histogram<double> JobDuration { get; }

    /// <summary>Disposes the underlying meter.</summary>
    public void Dispose()
    {
        this._meter.Dispose();
    }
}
