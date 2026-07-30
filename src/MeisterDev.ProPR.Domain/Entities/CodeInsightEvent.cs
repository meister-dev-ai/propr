// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     A persisted transition of a code-insight quality condition. The rows are the queryable contract a
///     notification or alerting capability consumes; Code Insights itself never delivers them and requires no
///     consumer to exist.
/// </summary>
/// <remarks>
///     <para>
///         Every row carries everything a downstream condition needs to evaluate and to phrase a message: the
///         scope, a provider-neutral metric name, the direction, the observed and previous values, the threshold
///         that fired, and the evidence behind it. That is deliberate: a consumer that had to read a Code Insight
///         table to make sense of an event would be coupled to internals that are free to change.
///     </para>
///     <para>
///         Rows are transitions, never restatements. A condition that stays true writes one row and nothing more
///         until it clears, because an event per evaluation would make the table useless as an alert source.
///     </para>
/// </remarks>
public sealed class CodeInsightEvent
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; init; }

    /// <summary>The client the condition was evaluated for.</summary>
    public Guid ClientId { get; init; }

    /// <summary>
    ///     Repository the condition is scoped to, or the empty string when it is client-wide. Non-null so the
    ///     scope of an event is always a complete key rather than one with holes in it.
    /// </summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>File the condition is scoped to, or the empty string when it is not file-scoped.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Which condition this row is a transition of.</summary>
    public CodeInsightEventType EventType { get; init; }

    /// <summary>Whether the condition became true or stopped being true.</summary>
    public CodeInsightConditionState State { get; init; }

    /// <summary>
    ///     Provider-neutral name of the measured quantity, e.g. <c>f1</c>, <c>false-positive-share</c>,
    ///     <c>finding-count</c>. A consumer can put this in a message without knowing our enums.
    /// </summary>
    public string Metric { get; init; } = string.Empty;

    /// <summary>Which way the measured value moved.</summary>
    public CodeInsightEventDirection Direction { get; init; }

    /// <summary>The value observed at the transition.</summary>
    public double ObservedValue { get; init; }

    /// <summary>
    ///     The value it moved from, or <see langword="null" /> when the condition has no prior value: a hotspot
    ///     crossing a threshold is a level, not a change.
    /// </summary>
    public double? PreviousValue { get; init; }

    /// <summary>How far the value moved, or how far past the threshold it sits when there is nothing to compare.</summary>
    public double Magnitude { get; init; }

    /// <summary>The configured threshold this transition crossed.</summary>
    public double ThresholdValue { get; init; }

    /// <summary>
    ///     How much evidence the observation rests on: sealed pull requests, resolved findings, or findings
    ///     counted. Carried so a consumer can ignore a thin signal instead of alerting on two data points.
    /// </summary>
    public int SampleSize { get; init; }

    /// <summary>Inclusive start of the window that was evaluated.</summary>
    public DateOnly WindowFrom { get; init; }

    /// <summary>Inclusive end of the window that was evaluated.</summary>
    public DateOnly WindowTo { get; init; }

    /// <summary>UTC instant the transition was observed.</summary>
    public DateTimeOffset OccurredAt { get; init; }
}
