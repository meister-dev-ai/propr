using System.Diagnostics.Metrics;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Api.Telemetry;

/// <summary>Exposes metrics for review jobs (histograms, counters, etc.).</summary>
public sealed class ReviewJobMetrics : IDisposable
{
    /// <summary>Histogram measuring review job durations in seconds.</summary>
    public readonly Histogram<double> JobDuration;

    private readonly Meter _meter;

    /// <summary>Creates the metrics meter and instruments.</summary>
    /// <param name="jobRepository">Repository used to observe queue depth.</param>
    public ReviewJobMetrics(IJobRepository jobRepository)
    {
        this._meter = new Meter("MeisterProPR", "1.0.0");
        this.JobDuration = this._meter.CreateHistogram<double>(
            "review_job_duration_seconds",
            "s",
            "Duration of review job processing");
        this._meter.CreateObservableGauge(
            "review_job_queue_depth",
            () => jobRepository.GetPendingJobs().Count,
            "jobs",
            "Number of jobs currently waiting to be processed");
    }

    /// <summary>Disposes the underlying meter.</summary>
    public void Dispose()
    {
        this._meter.Dispose();
    }
}