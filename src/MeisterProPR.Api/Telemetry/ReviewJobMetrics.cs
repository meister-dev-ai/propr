// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Telemetry;

/// <summary>Exposes metrics for review jobs (histograms, counters, etc.).</summary>
public sealed class ReviewJobMetrics : IDisposable
{
    private readonly Counter<long> _mentionScanCycles;
    private readonly Histogram<double> _mentionScanDuration;
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
        this._mentionScanCycles = this._meter.CreateCounter<long>(
            "meisterpropr_mention_scan_cycles_total",
            "cycles",
            "Total number of automatic mention scan cycles.");
        this._mentionScanDuration = this._meter.CreateHistogram<double>(
            "meisterpropr_mention_scan_duration_seconds",
            "s",
            "Duration of automatic mention scan cycles.");
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

    /// <summary>Records one review-job processing duration tagged with provider and outcome.</summary>
    public void RecordJobDuration(ScmProvider provider, double durationSeconds, string outcome)
    {
        var tags = CreateProviderTags(provider);
        tags.Add("job_outcome", outcome);
        this.JobDuration.Record(durationSeconds, tags);
    }

    /// <summary>Records one crawl duration tagged with provider.</summary>
    public void RecordCrawlDuration(ScmProvider provider, double durationSeconds)
    {
        this.CrawlDuration.Record(durationSeconds, CreateProviderTags(provider));
    }

    /// <summary>Increments the discovered-PR counter for one provider.</summary>
    public void AddCrawlPrsDiscovered(ScmProvider provider, long discoveredPrCount)
    {
        this.CrawlPrsDiscovered.Add(discoveredPrCount, CreateProviderTags(provider));
    }

    /// <summary>Records one mention-scan cycle tagged with provider scope and outcome.</summary>
    public void RecordMentionScanCycle(
        string providerScope,
        double durationSeconds,
        string outcome,
        int activeConfigCount)
    {
        var tags = CreateProviderScopeTags(providerScope, outcome, activeConfigCount);
        this._mentionScanCycles.Add(1, tags);
        this._mentionScanDuration.Record(durationSeconds, tags);
    }

    private static TagList CreateProviderScopeTags(string providerScope, string outcome, int activeConfigCount)
    {
        var tags = new TagList
        {
            { ReviewJobTelemetry.ScmProviderTagName, providerScope },
            { "scan_outcome", outcome },
            { "active_config_count", activeConfigCount },
        };

        return tags;
    }

    private static TagList CreateProviderTags(ScmProvider provider)
    {
        var tags = new TagList
        {
            { ReviewJobTelemetry.ScmProviderTagName, ReviewJobTelemetry.ToProviderTag(provider) },
        };

        return tags;
    }

    private static IEnumerable<Measurement<int>> ObserveQueueDepth(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var pendingJobs = jobRepository.GetPendingJobs();

        var measurements = new List<Measurement<int>>
        {
            new(pendingJobs.Count),
        };

        foreach (var jobsByProvider in pendingJobs.GroupBy(job => job.Provider))
        {
            measurements.Add(new Measurement<int>(jobsByProvider.Count(), CreateProviderTags(jobsByProvider.Key)));
        }

        return measurements;
    }
}
