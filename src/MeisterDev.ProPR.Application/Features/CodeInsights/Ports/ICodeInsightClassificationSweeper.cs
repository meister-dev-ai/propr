// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>What one classification sweep did, reported so a backlog is visible rather than inferred.</summary>
/// <param name="Considered">Findings picked up from the backlog this sweep.</param>
/// <param name="Classified">Findings that came back with a usable classification.</param>
/// <param name="Failed">Findings whose attempt did not produce one; each will be retried until it runs out of attempts.</param>
/// <param name="SkippedByGate">Findings left alone because their client's collection gate is closed.</param>
/// <param name="BacklogRemaining">Findings still awaiting classification and still retryable after this sweep.</param>
public sealed record CodeInsightClassificationSweepResult(
    int Considered,
    int Classified,
    int Failed,
    int SkippedByGate,
    int BacklogRemaining);

/// <summary>
///     Drains the type-classification backlog: picks up collected findings that carry no type yet and
///     classifies them post-hoc, off the review path.
/// </summary>
public interface ICodeInsightClassificationSweeper
{
    /// <summary>
    ///     Classifies one bounded batch of unclassified findings and returns what it did. Never throws except
    ///     for cancellation: a sweep that fails must not tear down the loop that calls it.
    /// </summary>
    Task<CodeInsightClassificationSweepResult> SweepOnceAsync(CancellationToken ct = default);
}
