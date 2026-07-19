// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

/// <summary>
///     Composes the registered finding-finalization checks over the deterministic gate's decisions. Each finding's
///     base decision is folded through the checks in registration order, so a later check sees the refinements of
///     the earlier ones; observations are recorded to the job protocol. The base gate's own logic is never
///     touched — new checks (e.g. a structural-claim verification) join by being registered, without editing this
///     class or any existing check.
/// </summary>
public sealed class ReviewFindingFinalizationPipeline(
    IEnumerable<IFindingFinalizationCheck> checks,
    IProtocolRecorder protocolRecorder) : IReviewFindingFinalizationPipeline
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<IFindingFinalizationCheck> _checks = checks?.ToArray() ?? throw new ArgumentNullException(nameof(checks));
    private readonly IProtocolRecorder _protocolRecorder = protocolRecorder ?? throw new ArgumentNullException(nameof(protocolRecorder));

    /// <inheritdoc />
    public async Task<IReadOnlyList<FinalGateDecision>> ApplyAsync(
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> baseDecisions,
        Guid? protocolId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(baseDecisions);

        if (_checks.Count == 0)
        {
            return baseDecisions;
        }

        var findingsById = new Dictionary<string, CandidateReviewFinding>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            findingsById[finding.FindingId] = finding;
        }

        var results = new List<FinalGateDecision>(baseDecisions.Count);
        var observations = new List<FinalizationObservation>();

        foreach (var decision in baseDecisions)
        {
            if (!findingsById.TryGetValue(decision.FindingId, out var finding))
            {
                results.Add(decision);
                continue;
            }

            var current = decision;
            foreach (var check in _checks)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = check.Evaluate(finding, current);
                current = outcome.Decision;
                if (outcome.Observation is not null)
                {
                    observations.Add(outcome.Observation);
                }
            }

            results.Add(current);
        }

        if (protocolId.HasValue && observations.Count > 0)
        {
            await this.RecordObservationsAsync(protocolId.Value, observations, ct).ConfigureAwait(false);
        }

        return results;
    }

    private async Task RecordObservationsAsync(Guid protocolId, IReadOnlyList<FinalizationObservation> observations, CancellationToken ct)
    {
        foreach (var observation in observations)
        {
            var payload = JsonSerializer.Serialize(observation, SerializerOptions);
            await _protocolRecorder.RecordReviewFindingGateEventAsync(
                protocolId,
                ReviewProtocolEventNames.FindingFinalizationCheck,
                payload,
                payload,
                null,
                ct).ConfigureAwait(false);
        }
    }
}
