// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Routed verification unit for one extracted claim.
/// </summary>
public sealed record VerificationWorkItem
{
    /// <summary>
    ///     Evidence scope limited to the anchor location.
    /// </summary>
    public const string AnchorOnlyScope = "AnchorOnly";

    /// <summary>
    ///     Evidence scope spanning multiple files.
    /// </summary>
    public const string CrossFileScope = "CrossFile";

    /// <summary>
    ///     Evidence scope requiring symbol-aware context.
    /// </summary>
    public const string SymbolAwareScope = "SymbolAware";

    /// <summary>
    ///     Verifier family for deterministic structural checks.
    /// </summary>
    public const string DeterministicStructureVerifier = "DeterministicStructure";

    /// <summary>
    ///     Verifier family for repository evidence collection.
    /// </summary>
    public const string RepositoryEvidenceVerifier = "RepositoryEvidence";

    /// <summary>
    ///     Verifier family for symbol-context collection.
    /// </summary>
    public const string SymbolContextVerifier = "SymbolContext";

    /// <summary>
    ///     Verifier family for bounded AI verification.
    /// </summary>
    public const string BoundedAiVerifier = "BoundedAi";

    /// <summary>
    ///     Initializes a routed verification work item.
    /// </summary>
    /// <param name="claim">Claim being verified.</param>
    /// <param name="findingProvenance">Provenance of the source finding.</param>
    /// <param name="verificationStage">Review stage for verification.</param>
    /// <param name="evidenceScope">Evidence scope required by the work item.</param>
    /// <param name="supportsAiMicroVerification">Whether bounded AI verification is allowed.</param>
    /// <param name="existingEvidence">Optional evidence already attached to the finding.</param>
    /// <param name="verifierFamilies">Optional verifier families to run.</param>
    /// <param name="existingHints">Optional routing hints already known for the work item.</param>
    public VerificationWorkItem(
        ClaimDescriptor claim,
        CandidateFindingProvenance findingProvenance,
        string verificationStage,
        string evidenceScope,
        bool supportsAiMicroVerification,
        EvidenceReference? existingEvidence = null,
        IReadOnlyList<string>? verifierFamilies = null,
        IReadOnlyList<string>? existingHints = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(findingProvenance);

        if (string.IsNullOrWhiteSpace(verificationStage))
        {
            throw new ArgumentException("Verification stage is required.", nameof(verificationStage));
        }

        if (!string.Equals(claim.Stage, verificationStage, StringComparison.Ordinal))
        {
            throw new ArgumentException("Verification stage must match the claim stage.", nameof(verificationStage));
        }

        if (string.IsNullOrWhiteSpace(evidenceScope))
        {
            throw new ArgumentException("Evidence scope is required.", nameof(evidenceScope));
        }

        this.Claim = claim;
        this.FindingProvenance = findingProvenance;
        this.VerificationStage = verificationStage;
        this.EvidenceScope = evidenceScope;
        this.SupportsAiMicroVerification = supportsAiMicroVerification;
        this.ExistingEvidence = existingEvidence;
        this.VerifierFamilies = verifierFamilies?.Where(family => !string.IsNullOrWhiteSpace(family)).ToArray() ??
            BuildDefaultVerifierFamilies(evidenceScope, supportsAiMicroVerification);
        this.ExistingHints = existingHints?.Where(hint => !string.IsNullOrWhiteSpace(hint)).ToArray() ?? [];
    }

    /// <summary>
    ///     Gets the claim being verified.
    /// </summary>
    public ClaimDescriptor Claim { get; }

    /// <summary>
    ///     Gets the provenance of the source finding.
    /// </summary>
    public CandidateFindingProvenance FindingProvenance { get; }

    /// <summary>
    ///     Gets the review stage for verification.
    /// </summary>
    public string VerificationStage { get; }

    /// <summary>
    ///     Gets the evidence scope required by the work item.
    /// </summary>
    public string EvidenceScope { get; }

    /// <summary>
    ///     Gets a value indicating whether bounded AI verification is allowed.
    /// </summary>
    public bool SupportsAiMicroVerification { get; }

    /// <summary>
    ///     Gets evidence already attached to the finding when available.
    /// </summary>
    public EvidenceReference? ExistingEvidence { get; }

    /// <summary>
    ///     Gets the verifier families selected for the work item.
    /// </summary>
    public IReadOnlyList<string> VerifierFamilies { get; }

    /// <summary>
    ///     Gets routing hints already known for the work item.
    /// </summary>
    public IReadOnlyList<string> ExistingHints { get; }

    private static IReadOnlyList<string> BuildDefaultVerifierFamilies(string evidenceScope, bool supportsAiMicroVerification)
    {
        var families = new List<string> { DeterministicStructureVerifier };

        if (!string.Equals(evidenceScope, AnchorOnlyScope, StringComparison.Ordinal))
        {
            families.Add(RepositoryEvidenceVerifier);
        }

        if (string.Equals(evidenceScope, SymbolAwareScope, StringComparison.Ordinal))
        {
            families.Add(SymbolContextVerifier);
        }

        if (supportsAiMicroVerification)
        {
            families.Add(BoundedAiVerifier);
        }

        return families;
    }
}
