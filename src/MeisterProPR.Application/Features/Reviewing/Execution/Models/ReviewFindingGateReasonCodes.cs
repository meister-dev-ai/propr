// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable machine-readable reason codes used by deterministic final-gate decisions.
/// </summary>
public static class ReviewFindingGateReasonCodes
{
    public const string DefaultPublish = "default_publish";
    public const string EvidenceResolved = "evidence_resolved";
    public const string VerifiedBoundedClaimSupport = "verified_bounded_claim_support";
    public const string MissingVerifiedClaimSupport = "missing_verified_claim_support";
    public const string MissingMultiFileEvidence = "missing_multi_file_evidence";
    public const string WeakBroadFinding = "weak_broad_finding";
    public const string NonActionableFinding = "non_actionable_finding";
    public const string InvariantContradiction = "invariant_contradiction";
    public const string VerificationDegraded = "verification_degraded";
}
