// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Events;
using MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Events;

/// <summary>
///     Evaluates the quality conditions for one client and records the transitions.
/// </summary>
/// <remarks>
///     <para>
///         Every condition is evaluated the same way: compute the current truth from the durable records, read
///         the state the last transition left it in, and write a row only when the two disagree. That is what
///         "fire once" means here: a condition that stays true for a month is one row, and its clearing is
///         another, which is also the recovery signal any alerting integration needs.
///     </para>
///     <para>
///         Nothing here reads or writes anything the collection or metric paths depend on, so a failed evaluation
///         costs one cycle of alerting latency and nothing else.
///     </para>
/// </remarks>
public sealed partial class CodeInsightConditionEvaluator(
    ICodeInsightMetricReader metricReader,
    ICodeInsightRollupReader rollupReader,
    ICodeInsightEventStore eventStore,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightConditionEvaluator> logger) : ICodeInsightConditionEvaluator
{
    /// <summary>Provider-neutral metric names, so a consumer never has to switch on our enums.</summary>
    private const string CorrectnessMetric = "f1";

    private const string FalsePositiveShareMetric = "false-positive-share";
    private const string FindingCountMetric = "finding-count";

    /// <summary>How many hotspot candidates one evaluation considers. A ranking, not a sweep.</summary>
    private const int HotspotCandidates = 10;

    public async Task<int> EvaluateAsync(
        Guid clientId,
        DateOnly asOf,
        CodeInsightConditionThresholds thresholds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        try
        {
            if (!await gate.IsCollectionEnabledAsync(clientId, ct))
            {
                return 0;
            }

            var window = new CodeInsightRollupQuery(
                [clientId],
                asOf.AddDays(-Math.Max(thresholds.WindowDays, 1)),
                asOf);

            var recorded = 0;
            recorded += await this.EvaluateCorrectnessAsync(clientId, window, thresholds, ct);
            recorded += await this.EvaluateFalsePositiveShareAsync(clientId, window, thresholds, ct);
            recorded += await this.EvaluateHotspotsAsync(clientId, window, thresholds, ct);
            return recorded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An evaluation is a derived convenience. Losing one costs a cycle of latency; the next one recomputes
            // from the same durable records.
            LogEvaluationFailed(logger, clientId, ex);
            return 0;
        }
    }

    /// <summary>
    ///     Correctness falling across the window. Compares the first and last buckets that clear the sample floor,
    ///     so a quiet week cannot raise an alert about the reviewer on the strength of two closed pull requests.
    /// </summary>
    private async Task<int> EvaluateCorrectnessAsync(
        Guid clientId,
        CodeInsightRollupQuery window,
        CodeInsightConditionThresholds thresholds,
        CancellationToken ct)
    {
        var series = await metricReader.GetCorrectnessSeriesAsync(window, CodeInsightBucketSize.Week, ct);
        var qualifying = series
            .Where(point => point.Result.SampleSize >= Math.Max(thresholds.MinimumSealedPullRequests, 1)
                            && point.Result.Metrics.F1 is not null)
            .OrderBy(point => point.BucketStart)
            .ToList();

        if (qualifying.Count < 2)
        {
            // Not enough comparable periods to say anything. Deliberately not a clearing either: absence of
            // evidence must not read as recovery.
            return 0;
        }

        var first = qualifying[0].Result;
        var last = qualifying[^1].Result;
        var decline = first.Metrics.F1!.Value - last.Metrics.F1!.Value;

        return await this.RecordAsync(
            CodeInsightEventScope.ForClient(clientId),
            CodeInsightEventType.CorrectnessDeclining,
            isTrue: decline >= thresholds.CorrectnessDeclineThreshold,
            CorrectnessMetric,
            CodeInsightEventDirection.Fell,
            last.Metrics.F1.Value,
            first.Metrics.F1.Value,
            Math.Abs(decline),
            thresholds.CorrectnessDeclineThreshold,
            last.SampleSize,
            window,
            ct);
    }

    /// <summary>
    ///     The share of resolved findings judged wrong. Distinct from correctness falling: precision can hold
    ///     steady while the reviewer becomes noisier in absolute terms, and a team feels the noise either way.
    /// </summary>
    private async Task<int> EvaluateFalsePositiveShareAsync(
        Guid clientId,
        CodeInsightRollupQuery window,
        CodeInsightConditionThresholds thresholds,
        CancellationToken ct)
    {
        var acceptance = await metricReader.GetAcceptanceAsync(window, ct);
        var resolved = acceptance.Metrics.Inputs.Resolved;

        if (resolved == 0)
        {
            return 0;
        }

        var share = (double)acceptance.Metrics.Inputs.FalsePositive / resolved;

        return await this.RecordAsync(
            CodeInsightEventScope.ForClient(clientId),
            CodeInsightEventType.FalsePositiveShareHigh,
            isTrue: share >= thresholds.FalsePositiveShareThreshold,
            FalsePositiveShareMetric,
            CodeInsightEventDirection.Rose,
            share,
            previousValue: null,
            Math.Abs(share - thresholds.FalsePositiveShareThreshold),
            thresholds.FalsePositiveShareThreshold,
            resolved,
            window,
            ct);
    }

    /// <summary>
    ///     Files accumulating more findings in the window than the threshold. Scoped per file, so one noisy file
    ///     firing does not mask another.
    /// </summary>
    private async Task<int> EvaluateHotspotsAsync(
        Guid clientId,
        CodeInsightRollupQuery window,
        CodeInsightConditionThresholds thresholds,
        CancellationToken ct)
    {
        var ranked = await rollupReader.GetConcentrationAsync(
            window,
            CodeInsightGrain.File,
            HotspotCandidates,
            ct);

        var recorded = 0;

        foreach (var row in ranked)
        {
            // A pull-request-level finding has no file, and "the empty path is a hotspot" would be a meaningless
            // alert.
            if (string.IsNullOrEmpty(row.FilePath))
            {
                continue;
            }

            recorded += await this.RecordAsync(
                CodeInsightEventScope.ForFile(clientId, row.RepositoryId, row.FilePath),
                CodeInsightEventType.ConcentrationHotspot,
                isTrue: row.Count >= thresholds.ConcentrationThreshold,
                FindingCountMetric,
                CodeInsightEventDirection.Rose,
                row.Count,
                previousValue: null,
                Math.Abs(row.Count - thresholds.ConcentrationThreshold),
                thresholds.ConcentrationThreshold,
                row.Count,
                window,
                ct);
        }

        // A file that fell out of the ranking is not cleared here. It would take a scan of every previously
        // firing file to notice, and the next evaluation clears it as soon as it reappears below the threshold,
        // which the top-N ranking guarantees for a file whose count is falling but still material.
        return recorded;
    }

    /// <summary>
    ///     Writes a transition when, and only when, the condition's truth differs from the state its last
    ///     transition left it in.
    /// </summary>
    private async Task<int> RecordAsync(
        CodeInsightEventScope scope,
        CodeInsightEventType eventType,
        bool isTrue,
        string metric,
        CodeInsightEventDirection direction,
        double observedValue,
        double? previousValue,
        double magnitude,
        double thresholdValue,
        int sampleSize,
        CodeInsightRollupQuery window,
        CancellationToken ct)
    {
        var current = await eventStore.GetCurrentStateAsync(scope, eventType, ct);
        var wasFiring = current == CodeInsightConditionState.Firing;

        if (isTrue == wasFiring)
        {
            // Already in this state. An event per evaluation would make the table useless as an alert source.
            return 0;
        }

        if (!isTrue && current is null)
        {
            // Never fired, so there is nothing to clear.
            return 0;
        }

        var transition = new CodeInsightEvent
        {
            Id = Guid.CreateVersion7(),
            ClientId = scope.ClientId,
            RepositoryId = scope.RepositoryId,
            FilePath = scope.FilePath,
            EventType = eventType,
            State = isTrue ? CodeInsightConditionState.Firing : CodeInsightConditionState.Cleared,
            Metric = metric,
            Direction = direction,
            ObservedValue = observedValue,
            PreviousValue = previousValue,
            Magnitude = magnitude,
            ThresholdValue = thresholdValue,
            SampleSize = sampleSize,
            WindowFrom = window.From,
            WindowTo = window.To,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        await eventStore.AppendAsync(transition, ct);
        LogTransitionRecorded(logger, eventType, transition.State, scope.ClientId, observedValue);
        return 1;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Code-insight condition {EventType} is now {State} for client {ClientId} at {ObservedValue}.")]
    private static partial void LogTransitionRecorded(
        ILogger logger,
        CodeInsightEventType eventType,
        CodeInsightConditionState state,
        Guid clientId,
        double observedValue);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Evaluating code-insight quality conditions for client {ClientId} failed; "
                  + "the next cycle recomputes them.")]
    private static partial void LogEvaluationFailed(ILogger logger, Guid clientId, Exception ex);
}
