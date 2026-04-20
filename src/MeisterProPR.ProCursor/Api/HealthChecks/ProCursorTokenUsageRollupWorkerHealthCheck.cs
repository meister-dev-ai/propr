// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.HealthChecks;

/// <summary>
///     Health check that reports whether the ProCursor token usage rollup worker is running and making progress.
/// </summary>
public sealed class ProCursorTokenUsageRollupWorkerHealthCheck(
    ProCursorTokenUsageRollupWorker worker,
    IOptions<ProCursorTokenUsageOptions> options) : IHealthCheck
{
    private readonly TimeSpan _staleCycleThreshold =
        TimeSpan.FromSeconds(Math.Max(1, options.Value.RollupPollSeconds) * 2);

    /// <summary>Checks worker health and returns a HealthCheckResult.</summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["lastCycleStartedAt"] = worker.LastCycleStartedAt?.ToString("O") ?? "not-started",
            ["lastCycleCompletedAt"] = worker.LastCycleCompletedAt?.ToString("O") ?? "not-completed",
            ["lastRetentionRunAtUtc"] = worker.LastRetentionRunAtUtc?.ToString("O") ?? "not-run",
            ["staleCycleThresholdSeconds"] = this._staleCycleThreshold.TotalSeconds,
        };

        if (!worker.IsRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("ProCursor token usage rollup worker is not running.", data: data));
        }

        if (!worker.LastCycleStartedAt.HasValue)
        {
            return Task.FromResult(HealthCheckResult.Degraded("ProCursor token usage rollup worker is starting.", data: data));
        }

        var now = DateTimeOffset.UtcNow;
        if (!worker.LastCycleCompletedAt.HasValue && now - worker.LastCycleStartedAt.Value > this._staleCycleThreshold)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy(
                    "ProCursor token usage rollup worker appears stuck in its current cycle.",
                    data: data));
        }

        if (worker.LastCycleCompletedAt.HasValue && now - worker.LastCycleCompletedAt.Value > this._staleCycleThreshold)
        {
            return Task.FromResult(
                HealthCheckResult.Degraded(
                    "ProCursor token usage rollup worker has not completed a recent cycle.",
                    data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("ProCursor token usage rollup worker is running.", data));
    }
}
