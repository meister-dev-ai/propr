// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The result of running one finding-finalization check against a candidate finding: the decision the check
///     leaves in place (unchanged, refined, or replaced) plus an optional observation the pipeline records to the
///     job protocol for traceability.
/// </summary>
/// <param name="Decision">The decision after this check ran — the same instance when the check made no change.</param>
/// <param name="Observation">An optional observation to record, or <see langword="null" /> when nothing is noteworthy.</param>
public sealed record FinalizationCheckOutcome(FinalGateDecision Decision, FinalizationObservation? Observation = null)
{
    /// <summary>
    ///     Creates an outcome that leaves the decision unchanged and records nothing.
    /// </summary>
    /// <param name="decision">The decision to carry forward unchanged.</param>
    /// <returns>An unchanged outcome.</returns>
    public static FinalizationCheckOutcome Unchanged(FinalGateDecision decision)
    {
        return new FinalizationCheckOutcome(decision);
    }
}

/// <summary>
///     A structured observation emitted by a finalization check for a single finding, recorded as a job protocol
///     event so the check's verdict (and its reason) is visible in the trace.
/// </summary>
/// <param name="CheckName">Stable name of the check that produced the observation.</param>
/// <param name="FindingId">Identifier of the finding the observation concerns.</param>
/// <param name="Outcome">Machine-readable verdict (e.g. <c>verified</c>, <c>unverified</c>, <c>contradicted</c>).</param>
/// <param name="Detail">Optional human-readable detail describing the verdict.</param>
public sealed record FinalizationObservation(string CheckName, string FindingId, string Outcome, string? Detail = null);
