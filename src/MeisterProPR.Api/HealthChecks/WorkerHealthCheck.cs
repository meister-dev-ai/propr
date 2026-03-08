using MeisterProPR.Api.Workers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MeisterProPR.Api.HealthChecks;

/// <summary>
///     Health check that reports whether the background review worker is running.
/// </summary>
public sealed class WorkerHealthCheck(ReviewJobWorker worker) : IHealthCheck
{
    /// <summary>Checks worker health and returns a HealthCheckResult.</summary>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = worker.IsRunning
            ? HealthCheckResult.Healthy("Worker is running.")
            : HealthCheckResult.Unhealthy("Worker is not running.");
        return Task.FromResult(result);
    }
}