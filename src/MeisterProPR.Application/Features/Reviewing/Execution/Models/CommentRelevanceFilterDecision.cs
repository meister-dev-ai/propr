// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The final keep-or-discard decision for one normalized review comment.
/// </summary>
public sealed record CommentRelevanceFilterDecision
{
    /// <summary>Canonical keep decision literal.</summary>
    public const string KeepDecision = "Keep";

    /// <summary>Canonical discard decision literal.</summary>
    public const string DiscardDecision = "Discard";

    /// <summary>Canonical decision-source literal for deterministic screening.</summary>
    public const string DeterministicScreeningSource = "deterministic_screening";

    /// <summary>Canonical decision-source literal for AI adjudication.</summary>
    public const string AiAdjudicationSource = "ai_adjudication";

    /// <summary>Canonical decision-source literal for fail-open fallback handling.</summary>
    public const string FallbackModeSource = "fallback_mode";

    /// <summary>
    ///     Creates a new <see cref="CommentRelevanceFilterDecision" />.
    /// </summary>
    public CommentRelevanceFilterDecision(
        string decision,
        ReviewComment originalComment,
        IReadOnlyList<string>? reasonCodes,
        string decisionSource)
    {
        if (!string.Equals(decision, KeepDecision, StringComparison.Ordinal) &&
            !string.Equals(decision, DiscardDecision, StringComparison.Ordinal))
        {
            throw new ArgumentException("Decision must be Keep or Discard.", nameof(decision));
        }

        if (string.IsNullOrWhiteSpace(decisionSource))
        {
            throw new ArgumentException("Decision source is required.", nameof(decisionSource));
        }

        var copiedReasonCodes = reasonCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).ToArray() ?? [];
        if (string.Equals(decision, DiscardDecision, StringComparison.Ordinal) && copiedReasonCodes.Length == 0)
        {
            throw new ArgumentException("Discard decisions must include at least one reason code.", nameof(reasonCodes));
        }

        this.Decision = decision;
        this.OriginalComment = originalComment ?? throw new ArgumentNullException(nameof(originalComment));
        this.ReasonCodes = copiedReasonCodes;
        this.DecisionSource = decisionSource;
    }

    /// <summary>The final keep-or-discard decision.</summary>
    public string Decision { get; }

    /// <summary>The original normalized comment being evaluated.</summary>
    public ReviewComment OriginalComment { get; }

    /// <summary>Machine-readable reason codes supporting the decision.</summary>
    public IReadOnlyList<string> ReasonCodes { get; }

    /// <summary>Whether the decision came from deterministic screening, AI adjudication, or fallback mode.</summary>
    public string DecisionSource { get; }

    /// <summary>True when the comment remains actionable after filtering.</summary>
    public bool IsKeep => string.Equals(this.Decision, KeepDecision, StringComparison.Ordinal);

    /// <summary>True when the comment is discarded by the filter.</summary>
    public bool IsDiscard => string.Equals(this.Decision, DiscardDecision, StringComparison.Ordinal);
}
