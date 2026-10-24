// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Bounded independent correctness evidence. Source grounding and the counter-check status are server-owned.</summary>
public sealed record VerificationConfidenceEvidence(
    FindingConfidence Level,
    string Witness,
    string Consequence,
    string Counterevidence,
    IReadOnlyList<string> Assumptions,
    bool AllClaimsCovered,
    bool SourceGrounded,
    bool ContradictionCheckPassed = false)
{
    /// <summary>Maximum characters in an optional evidence field, matching the retained rationale limit.</summary>
    public const int MaxTextLength = FindingRiskAssessment.MaxReasonLength;

    /// <summary>Maximum unresolved assumptions accepted from one verification response.</summary>
    public const int MaxAssumptions = 8;

    /// <summary>Maximum exact source excerpts accepted to establish grounding.</summary>
    public const int MaxSourceQuotes = 4;

    /// <summary>Checks that evidence text is nonblank and fits the bounded model response contract.</summary>
    public static bool IsValidText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxTextLength;

    /// <summary>Checks independent source coverage and bounded evidence before assigning correctness confidence.</summary>
    public bool HasCompleteEvidence() => this.SourceGrounded && this.AllClaimsCovered
                                                             && this.Level is >= FindingConfidence.Low and <= FindingConfidence.Certain
                                                             && IsValidText(this.Witness) && IsValidText(this.Consequence) && IsValidText(this.Counterevidence)
                                                             && this.Assumptions is { Count: <= MaxAssumptions } && this.Assumptions.All(IsValidText);
}

/// <summary>Selected runtime identity, independent of the model that generated the finding.</summary>
public sealed record VerificationRuntimeProvenance(
    string? ModelId,
    Guid? ConnectionId,
    string Purpose,
    string? LogicalModelName,
    bool Fallback);

/// <summary>A bounded synthesis request to reconsider a retained finding.</summary>
public sealed record FindingVerificationChallenge(string FindingId, string Reason, string Counterevidence);
