// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Events;

/// <summary>
///     Evaluates the quality conditions for one client and records any transitions.
/// </summary>
/// <remarks>
///     Best-effort, behind the collection gate, and independent of sealing and projection: an evaluation that
///     fails costs at most one cycle of alerting latency, and the next one recomputes from the same durable
///     records.
/// </remarks>
public interface ICodeInsightConditionEvaluator
{
    /// <summary>
    ///     Evaluates every condition for <paramref name="clientId" /> over the window ending
    ///     <paramref name="asOf" />, and appends a transition for each condition whose state changed. Returns how
    ///     many transitions were recorded.
    /// </summary>
    Task<int> EvaluateAsync(
        Guid clientId,
        DateOnly asOf,
        CodeInsightConditionThresholds thresholds,
        CancellationToken ct = default);
}
