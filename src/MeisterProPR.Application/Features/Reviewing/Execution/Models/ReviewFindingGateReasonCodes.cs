// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable machine-readable reason codes used by deterministic final-gate decisions.
/// </summary>
public static class ReviewFindingGateReasonCodes
{
    /// <summary>
    ///     Reason code used when the default publish path is selected.
    /// </summary>
    public const string DefaultPublish = "default_publish";

    /// <summary>
    ///     Reason code used when evidence fully resolves the claim.
    /// </summary>
    public const string EvidenceResolved = "evidence_resolved";

    /// <summary>
    ///     Reason code used when bounded verification supported the claim.
    /// </summary>
    public const string VerifiedBoundedClaimSupport = "verified_bounded_claim_support";

    /// <summary>
    ///     Reason code used when bounded verification did not support the claim.
    /// </summary>
    public const string MissingVerifiedClaimSupport = "missing_verified_claim_support";

    /// <summary>
    ///     Reason code used when required multi-file evidence is missing.
    /// </summary>
    public const string MissingMultiFileEvidence = "missing_multi_file_evidence";

    /// <summary>
    ///     Reason code used when a finding is too broad to publish.
    /// </summary>
    public const string WeakBroadFinding = "weak_broad_finding";

    /// <summary>
    ///     Reason code used when a finding is not actionable.
    /// </summary>
    public const string NonActionableFinding = "non_actionable_finding";

    /// <summary>
    ///     Reason code used when an invariant contradiction blocks publication.
    /// </summary>
    public const string InvariantContradiction = "invariant_contradiction";

    /// <summary>
    ///     Reason code used when verification completed in a degraded state.
    /// </summary>
    public const string VerificationDegraded = "verification_degraded";
}
