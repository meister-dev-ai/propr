// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Records a single tool invocation made during an agentic review pass.
/// </summary>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="Arguments">Serialised arguments passed to the tool.</param>
/// <param name="Result">Serialised result returned by the tool.</param>
/// <param name="InvokedAt">UTC timestamp at which the tool was invoked.</param>
/// <param name="StartedAt">UTC timestamp at which execution started.</param>
/// <param name="CompletedAt">UTC timestamp at which execution completed, when observed.</param>
/// <param name="DurationMs">Total elapsed duration in milliseconds when captured.</param>
/// <param name="WaitDurationMs">Measured wait or backoff duration in milliseconds when available.</param>
/// <param name="ActiveDurationMs">Measured active execution duration in milliseconds when available.</param>
/// <param name="TimingAvailability">Availability state for the timing capture.</param>
/// <param name="Outcome">Terminal outcome of the tool invocation.</param>
/// <param name="PhaseTimings">Ordered phase timings captured within the invocation.</param>
public sealed record ReviewToolCall(
    string ToolName,
    string Arguments,
    string Result,
    DateTimeOffset InvokedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    long? DurationMs = null,
    long? WaitDurationMs = null,
    long? ActiveDurationMs = null,
    string? TimingAvailability = null,
    string? Outcome = null,
    IReadOnlyList<ProtocolEventPhaseTiming>? PhaseTimings = null);
