// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Records one individual step in the agentic review workflow.
///     Child entity of <see cref="ReviewJobProtocol" />.
/// </summary>
public sealed class ProtocolEvent
{
    /// <summary>Unique identifier for this event.</summary>
    public Guid Id { get; init; }

    /// <summary>The protocol this event belongs to.</summary>
    public Guid ProtocolId { get; init; }

    /// <summary>Whether this event represents an AI call or a tool invocation.</summary>
    public ProtocolEventKind Kind { get; init; }

    /// <summary>Human-readable name, e.g. <c>"ai-call-1"</c> or <c>"get_file_content"</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>UTC timestamp at which the event occurred.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Input token count for this event. <see langword="null" /> when the provider did not return usage data.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Output token count for this event. <see langword="null" /> when the provider did not return usage data.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Truncated sample of the input text sent (max 4,000 characters). <see langword="null" /> when not applicable.</summary>
    public string? InputTextSample { get; init; }

    /// <summary>
    ///     Truncated summary of the output or tool result (max 1,000 characters). <see langword="null" /> when not
    ///     applicable.
    /// </summary>
    public string? OutputSummary { get; init; }

    /// <summary>Error message if this step failed. <see langword="null" /> on success.</summary>
    public string? Error { get; init; }
}
