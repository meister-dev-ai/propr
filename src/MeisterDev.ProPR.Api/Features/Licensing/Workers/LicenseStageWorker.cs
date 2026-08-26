// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Reports when the installation moves from one license lifecycle stage to the next, and evaluates the
///     metered author allowance on the same cadence.
///     <para>
///         Capability resolution derives the stage on its own whenever it is asked, so nothing depends on this
///         sweep for correctness. What it adds is a log entry on an installation nobody is using: without it,
///         an expiry there would first be reported by whatever request happened to run next.
///     </para>
///     <para>
///         The sweep reads the license state through the provider and does not invalidate its cache. The
///         interval is longer than the provider's cache duration, so an idle installation's sweep is a cache
///         miss that loads, verifies and reads the licensing clock, which is what records the observed instant
///         and advances the rollback ratchet. On a busy installation another caller may have loaded within the
///         cache window, in which case the sweep is a cache hit that reads nothing and the ratchet was advanced
///         by that load moments earlier. The sweep therefore adds no work beyond the load the cache duration
///         already calls for.
///     </para>
///     <para>
///         Controlled by <c>LICENSE_STAGE_INTERVAL_SECONDS</c> (default 900, minimum 60, set to 0 to switch the
///         reporting off).
///     </para>
/// </summary>
public sealed partial class LicenseStageWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<LicenseStageWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 900;

    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    // What the last sweep observed, so a stage that persists is reported once rather than on every sweep. Held
    // on the worker, which is a singleton, so it covers one process: each replica reports the transition it
    // sees for itself.
    private LicenseStage? _lastReportedStage;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ResolveInterval(configuration) is not { } interval)
        {
            LogReportingDisabled(logger);
            return;
        }

        LogWorkerStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await this.SweepOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // The sweep passes cancellation through so that shutdown ends the worker. Cancellation
                    // raised while the host is not stopping came from something else, such as a timeout below
                    // the sweep. It is reported on the same path as any other sweep failure and the loop
                    // continues, so one such failure does not end the reporting for the life of the process.
                    LogSweepFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: either the wait was cancelled, or the sweep was cancelled while the host was
            // stopping.
        }
    }

    /// <summary>
    ///     How often the sweep runs, or <see langword="null" /> when reporting is switched off.
    ///     <para>
    ///         A positive value below the floor is raised to it, so no configured interval turns the reporting
    ///         into a poll. Zero and any negative value switch the reporting off rather than being clamped, so an
    ///         operator has a way to silence it. Nothing here covers a value that is not an integer or one large
    ///         enough to be outside the range a timer accepts; both fail at startup.
    ///     </para>
    /// </summary>
    /// <param name="configuration">Supplies <c>LICENSE_STAGE_INTERVAL_SECONDS</c>.</param>
    /// <returns>The interval, or <see langword="null" /> when the reporting is off.</returns>
    internal static TimeSpan? ResolveInterval(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var intervalSeconds = configuration.GetValue("LICENSE_STAGE_INTERVAL_SECONDS", DefaultIntervalSeconds);

        return intervalSeconds <= 0
            ? null
            : TimeSpan.FromSeconds(Math.Max(intervalSeconds, MinInterval.TotalSeconds));
    }

    /// <summary>
    ///     Reads the current stage, reports it when it differs from what the previous sweep saw, re-observes
    ///     the installation's system profile, and evaluates the author allowance.
    ///     <para>
    ///         Both follow the stage report, and each reports its own failures rather than throwing, so neither
    ///         a profile that cannot be read nor an allowance that cannot be evaluated changes the stage report
    ///         or the stage this sweep returns.
    ///     </para>
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The stage this sweep observed, or <see langword="null" /> when it could not be read.</returns>
    internal async Task<LicenseStage?> SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var stateProvider = scope.ServiceProvider.GetService<ILicenseStateProvider>();
            if (stateProvider is null)
            {
                return null;
            }

            var state = await stateProvider.GetStateAsync(ct);

            this.ReportTransition(state);

            await this.ObserveSystemProfileAsync(scope.ServiceProvider, ct);
            await this.EvaluateAuthorAllowanceAsync(scope.ServiceProvider, ct);

            return state.Stage;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed sweep must not stop the host. The next sweep retries, and capability resolution is
            // unaffected either way because it derives the stage for itself.
            LogSweepFailed(logger, exception);
            return null;
        }
    }

    /// <summary>
    ///     Re-observes the system profile, so an installation nobody is using still records a move it has made.
    ///     <para>
    ///         The observation needs the instant the installation's identity was created, which is one of the
    ///         components the profile records. An installation with no identity yet has nothing to record a
    ///         profile against, and is left for the read that creates one.
    ///     </para>
    ///     <para>
    ///         A failure here is kept away from the stage report: the profile decides nothing about what the
    ///         installation is entitled to, so it must not cost the sweep the stage it read.
    ///     </para>
    /// </summary>
    private async Task ObserveSystemProfileAsync(IServiceProvider services, CancellationToken ct)
    {
        var profileObserver = services.GetService<ISystemProfileObserver>();
        var identityStore = services.GetService<ILicensingIdentityStore>();

        if (profileObserver is null || identityStore is null)
        {
            return;
        }

        try
        {
            if (await identityStore.GetCreatedAtAsync(ct) is { } identityCreatedAt)
            {
                await profileObserver.ObserveAsync(identityCreatedAt, ct);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProfileObservationFailed(logger, exception);
        }
    }

    /// <summary>
    ///     Evaluates the current month's authors against the number the license states, so an installation
    ///     nobody is using still records and reports a month that goes above it.
    ///     <para>
    ///         The evaluation records and reports; it decides nothing about what the installation may run. A
    ///         failure here is therefore kept away from the stage report, the same way the profile observation
    ///         is, and the next sweep evaluates again.
    ///     </para>
    /// </summary>
    private async Task EvaluateAuthorAllowanceAsync(IServiceProvider services, CancellationToken ct)
    {
        if (services.GetService<IAuthorOverageEvaluator>() is not { } evaluator)
        {
            return;
        }

        try
        {
            await evaluator.EvaluateAsync(cancellationToken: ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAllowanceEvaluationFailed(logger, exception);
        }
    }

    /// <summary>
    ///     Reports the stage when it differs from what the previous sweep saw.
    ///     <para>
    ///         The three stages an operator has to act on are reported at warning level, in a form that names the
    ///         boundary instants rather than the stage that preceded them, because the instants are what the
    ///         operator acts on. The other two are reported at information level, and the first sweep of a
    ///         process has its own message there: it has no previous stage to name, and one worded as a
    ///         transition would report a change nothing observed.
    ///     </para>
    /// </summary>
    private void ReportTransition(LicenseState state)
    {
        var previousStage = this._lastReportedStage;
        this._lastReportedStage = state.Stage;

        if (previousStage == state.Stage)
        {
            return;
        }

        if (state.Stage is LicenseStage.Warning or LicenseStage.Grace or LicenseStage.Reverted)
        {
            LogLicenseStageNeedsAttention(logger, state.Stage, state.ExpiresAt, state.GraceEndsAt, state.DaysRemaining);
        }
        else if (previousStage is { } previous)
        {
            LogLicenseStageChanged(logger, previous, state.Stage);
        }
        else
        {
            LogLicenseStageObserved(logger, state.Stage);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "License stage reporting is disabled; the stage is still derived on every capability check.")]
    private static partial void LogReportingDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "License stage worker started (every {IntervalSeconds}s).")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "The license stage is {Stage}.")]
    private static partial void LogLicenseStageObserved(ILogger logger, LicenseStage stage);

    [LoggerMessage(Level = LogLevel.Information, Message = "The license stage changed from {PreviousStage} to {Stage}.")]
    private static partial void LogLicenseStageChanged(ILogger logger, LicenseStage previousStage, LicenseStage stage);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The license stage is now {Stage} (term ends {ExpiresAt}, grace ends {GraceEndsAt}, {DaysRemaining} day(s) left). Activate a renewed license to keep the commercial capabilities available.")]
    private static partial void LogLicenseStageNeedsAttention(
        ILogger logger,
        LicenseStage stage,
        DateTimeOffset? expiresAt,
        DateTimeOffset? graceEndsAt,
        int? daysRemaining);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The license stage sweep failed; the next sweep retries.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The installation's system profile could not be re-observed on this sweep; the next sweep retries. The recorded profile is unchanged and nothing about the license stage depends on it.")]
    private static partial void LogProfileObservationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The author allowance could not be evaluated on this sweep; the next sweep retries. Nothing has been withheld, delayed or degraded, and nothing about what the installation may run depends on the evaluation.")]
    private static partial void LogAllowanceEvaluationFailed(ILogger logger, Exception exception);
}
