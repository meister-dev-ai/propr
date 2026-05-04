// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Routed verification unit for one extracted claim.
/// </summary>
public sealed record VerificationWorkItem
{
    public const string AnchorOnlyScope = "AnchorOnly";
    public const string CrossFileScope = "CrossFile";
    public const string SymbolAwareScope = "SymbolAware";
    public const string DeterministicStructureVerifier = "DeterministicStructure";
    public const string RepositoryEvidenceVerifier = "RepositoryEvidence";
    public const string SymbolContextVerifier = "SymbolContext";
    public const string BoundedAiVerifier = "BoundedAi";

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

    public ClaimDescriptor Claim { get; }

    public CandidateFindingProvenance FindingProvenance { get; }

    public string VerificationStage { get; }

    public string EvidenceScope { get; }

    public bool SupportsAiMicroVerification { get; }

    public EvidenceReference? ExistingEvidence { get; }

    public IReadOnlyList<string> VerifierFamilies { get; }

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
