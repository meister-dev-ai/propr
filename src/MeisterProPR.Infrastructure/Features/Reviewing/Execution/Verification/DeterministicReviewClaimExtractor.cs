// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Extracts the small initial set of deterministic contradiction-backed claim families.
/// </summary>
public sealed class DeterministicReviewClaimExtractor : IReviewClaimExtractor
{
    private static readonly Regex IdentifierInBackticksRegex = new("`(?<identifier>[A-Za-z_][A-Za-z0-9_.]*)`", RegexOptions.Compiled);

    private static readonly Regex NamedProgramElementRegex = new(
        "\\b(?:method|function|helper|symbol|class|type|property|field|namespace)\\s+(?<identifier>[A-Za-z_][A-Za-z0-9_.]*)\\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MissingIdentifierRegex = new(
        "\\b(?<identifier>[A-Za-z_][A-Za-z0-9_.]*)\\b\\s+(?:is|are)\\s+missing\\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex[] SubjectIdentifierRegexes =
    [
        IdentifierInBackticksRegex,
        NamedProgramElementRegex,
        MissingIdentifierRegex,
    ];

    private static readonly string[] ApiOrSymbolUsageIndicators =
    [
        "symbol",
        "API",
        "method",
        "function",
        "helper",
        "using ",
        "namespace",
    ];

    private static readonly HashSet<string> FamiliesRequiringEvidence = new(StringComparer.Ordinal)
    {
        ClaimDescriptor.ApiOrSymbolUsageFamily,
        ClaimDescriptor.ConfigurationOrWiringFamily,
        ClaimDescriptor.CrossFileConsistencyFamily,
        ClaimDescriptor.TestAdequacyFamily,
        ClaimDescriptor.DocumentationAccuracyFamily,
    };

    private static readonly Dictionary<string, string> CategoryClaimFamilies = new(StringComparer.Ordinal)
    {
        ["architecture"] = ClaimDescriptor.CrossFileConsistencyFamily,
        ["configuration"] = ClaimDescriptor.ConfigurationOrWiringFamily,
        ["test"] = ClaimDescriptor.TestAdequacyFamily,
        ["documentation"] = ClaimDescriptor.DocumentationAccuracyFamily,
        ["robustness"] = ClaimDescriptor.OperationalRiskFamily,
    };

    private static readonly ClaimProfile CrossFileEvidenceRequiredProfile = new(
        CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
        ClaimDescriptor.CrossFileConsistencyFamily,
        ClaimDescriptor.NeedsEvidenceMode);

    private static readonly ClaimKindRule[] ClaimKindRules =
    [
        new(
            new ClaimProfile(
                CandidateReviewFinding.DockerFinalStageRootUserClaimKind,
                ClaimDescriptor.OperationalRiskFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            true,
            "Dockerfile",
            ["runs as root"]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.GitHubActionsSecretEchoClaimKind,
                ClaimDescriptor.OperationalRiskFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            true,
            RequiredPhrases: ["secret", "echo"]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.TerraformPublicIngressClaimKind,
                ClaimDescriptor.OperationalRiskFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            true,
            RequiredPhrases: ["public", "ingress"]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.ManifestLockfileMisalignmentClaimKind,
                ClaimDescriptor.ConfigurationOrWiringFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            true,
            RequiredAnyPhraseGroups: [["lockfile", "manifest"]]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.WiringMissingRegistrationClaimKind,
                ClaimDescriptor.ConfigurationOrWiringFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            true,
            RequiredAnyPhraseGroups:
            [
                ["registration", "dispatch", "wiring"],
                ["missing registration", "registration is missing", "dispatch registration", "handlers unregistered", "unregistered"],
            ]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.ShellUnquotedVariableClaimKind,
                ClaimDescriptor.OperationalRiskFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            true,
            RequiredPhrases: ["unquoted", "variable"]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
                ClaimDescriptor.CodeContractFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            RequiredPhrases: ["ReviewComment.Message", "null"]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.ReviewResultCommentsNullableClaimKind,
                ClaimDescriptor.CodeContractFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            RequiredPhrases: ["result comments", "null"]),
        new(
            new ClaimProfile(
                CandidateReviewFinding.ReviewFileResultsDuplicateExpectedClaimKind,
                ClaimDescriptor.CodeContractFamily,
                ClaimDescriptor.DeterministicOnlyMode),
            RequiredAnyPhraseGroups: [["Duplicate review_file_results", "duplicate review file results"]]),
    ];

    public IReadOnlyList<ClaimDescriptor> ExtractClaims(CandidateReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var claimProfile = ResolveClaimProfile(finding);
        if (claimProfile is null)
        {
            return [];
        }

        var stage = DetermineStage(finding);
        var subjectIdentifier = DetermineSubjectIdentifier(finding, claimProfile.ClaimFamily);

        var requiresCrossFileEvidence = string.Equals(
                                            claimProfile.ClaimKind, CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind, StringComparison.Ordinal)
                                        || (finding.Provenance.RequiresExplicitSupport &&
                                            string.Equals(stage, ClaimDescriptor.PrLevelStage, StringComparison.Ordinal) &&
                                            string.Equals(
                                                claimProfile.ClaimKind, CandidateReviewFinding.GenericReviewAssertionClaimKind, StringComparison.Ordinal));

        return
        [
            new ClaimDescriptor(
                $"{finding.FindingId}:claim:001",
                finding.FindingId,
                stage,
                claimProfile.ClaimKind,
                finding.Message,
                finding.Severity,
                claimProfile.VerificationMode,
                claimProfile.ClaimFamily,
                DetermineSubjectKind(claimProfile.ClaimFamily, subjectIdentifier),
                subjectIdentifier,
                finding.FilePath,
                finding.LineNumber,
                requiresCrossFileEvidence,
                string.Equals(claimProfile.ClaimFamily, ClaimDescriptor.ApiOrSymbolUsageFamily, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(subjectIdentifier)),
        ];
    }

    private static ClaimProfile ResolveClaimProfile(CandidateReviewFinding finding)
    {
        foreach (var rule in ClaimKindRules)
        {
            if (rule.Matches(finding))
            {
                return rule.Profile;
            }
        }

        if (string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal))
        {
            return CrossFileEvidenceRequiredProfile;
        }

        var claimFamily = DetermineFallbackClaimFamily(finding);
        return new ClaimProfile(
            CandidateReviewFinding.GenericReviewAssertionClaimKind,
            claimFamily,
            DetermineFallbackVerificationMode(finding, claimFamily));
    }

    private static string DetermineStage(CandidateReviewFinding finding)
    {
        return string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal)
               || finding.Evidence?.SupportingFiles.Count > 1
            ? ClaimDescriptor.PrLevelStage
            : ClaimDescriptor.LocalStage;
    }

    private static string DetermineFallbackVerificationMode(CandidateReviewFinding finding, string claimFamily)
    {
        if (finding.Provenance.RequiresExplicitSupport)
        {
            return ClaimDescriptor.NeedsEvidenceMode;
        }

        return FamiliesRequiringEvidence.Contains(claimFamily)
            ? ClaimDescriptor.NeedsEvidenceMode
            : ClaimDescriptor.DeterministicOnlyMode;
    }

    private static string DetermineFallbackClaimFamily(CandidateReviewFinding finding)
    {
        if (string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal) ||
            finding.Evidence?.SupportingFiles.Count > 1)
        {
            return ClaimDescriptor.CrossFileConsistencyFamily;
        }

        if (CategoryClaimFamilies.TryGetValue(finding.Category, out var claimFamily))
        {
            return claimFamily;
        }

        if (ContainsAnyPhrase(finding.Message, ApiOrSymbolUsageIndicators, StringComparison.OrdinalIgnoreCase))
        {
            return ClaimDescriptor.ApiOrSymbolUsageFamily;
        }

        return ClaimDescriptor.CodeContractFamily;
    }

    private static string? DetermineSubjectIdentifier(CandidateReviewFinding finding, string claimFamily)
    {
        if (!string.Equals(claimFamily, ClaimDescriptor.ApiOrSymbolUsageFamily, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var regex in SubjectIdentifierRegexes)
        {
            var match = regex.Match(finding.Message);
            if (match.Success)
            {
                return match.Groups["identifier"].Value;
            }
        }

        return null;
    }

    private static string DetermineSubjectKind(string claimFamily, string? subjectIdentifier)
    {
        return string.Equals(claimFamily, ClaimDescriptor.ApiOrSymbolUsageFamily, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(subjectIdentifier)
            ? "symbol"
            : "file_review_finding";
    }

    private static bool ContainsAnyPhrase(string text, IReadOnlyList<string> phrases, StringComparison comparison)
    {
        return phrases.Any(phrase => text.Contains(phrase, comparison));
    }

    private static bool ContainsAllPhrases(string text, IReadOnlyList<string> phrases, StringComparison comparison)
    {
        return phrases.All(phrase => text.Contains(phrase, comparison));
    }

    private sealed record ClaimProfile(string ClaimKind, string ClaimFamily, string VerificationMode);

    private sealed record ClaimKindRule(
        ClaimProfile Profile,
        bool RequiresExplicitSupport = false,
        string? FilePathSuffix = null,
        IReadOnlyList<string>? RequiredPhrases = null,
        IReadOnlyList<IReadOnlyList<string>>? RequiredAnyPhraseGroups = null)
    {
        public bool Matches(CandidateReviewFinding finding)
        {
            if (this.RequiresExplicitSupport && !finding.Provenance.RequiresExplicitSupport)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(this.FilePathSuffix) &&
                !(finding.FilePath?.EndsWith(this.FilePathSuffix, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return false;
            }

            if (this.RequiredPhrases is { Count: > 0 } &&
                !ContainsAllPhrases(finding.Message, this.RequiredPhrases, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (this.RequiredAnyPhraseGroups is { Count: > 0 } &&
                this.RequiredAnyPhraseGroups.Any(group => !ContainsAnyPhrase(finding.Message, group, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
        }
    }
}
