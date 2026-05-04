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

    public IReadOnlyList<ClaimDescriptor> ExtractClaims(CandidateReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var claimKind = TryResolveClaimKind(finding);
        if (claimKind is null)
        {
            return [];
        }

        var claimFamily = DetermineClaimFamily(claimKind, finding);
        var subjectIdentifier = DetermineSubjectIdentifier(finding, claimFamily);

        return
        [
            new ClaimDescriptor(
                $"{finding.FindingId}:claim:001",
                finding.FindingId,
                DetermineStage(finding),
                claimKind,
                finding.Message,
                finding.Severity,
                DetermineVerificationMode(claimKind, claimFamily),
                claimFamily,
                subjectKind: DetermineSubjectKind(claimFamily, subjectIdentifier),
                subjectIdentifier: subjectIdentifier,
                anchorFilePath: finding.FilePath,
                anchorLineNumber: finding.LineNumber,
                requiresCrossFileEvidence: string.Equals(claimKind, CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind, StringComparison.Ordinal),
                requiresSymbolEvidence: string.Equals(claimFamily, ClaimDescriptor.ApiOrSymbolUsageFamily, StringComparison.Ordinal) &&
                                        !string.IsNullOrWhiteSpace(subjectIdentifier)),
        ];
    }

    private static string? TryResolveClaimKind(CandidateReviewFinding finding)
    {
        if (finding.Message.Contains("ReviewComment.Message", StringComparison.Ordinal) &&
            finding.Message.Contains("null", StringComparison.OrdinalIgnoreCase))
        {
            return CandidateReviewFinding.ReviewCommentMessageNullableClaimKind;
        }

        if (finding.Message.Contains("result comments", StringComparison.OrdinalIgnoreCase) &&
            finding.Message.Contains("null", StringComparison.OrdinalIgnoreCase))
        {
            return CandidateReviewFinding.ReviewResultCommentsNullableClaimKind;
        }

        if (finding.Message.Contains("Duplicate review_file_results", StringComparison.OrdinalIgnoreCase) ||
            finding.Message.Contains("duplicate review file results", StringComparison.OrdinalIgnoreCase))
        {
            return CandidateReviewFinding.ReviewFileResultsDuplicateExpectedClaimKind;
        }

        if (string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal))
        {
            return CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind;
        }

        return CandidateReviewFinding.GenericReviewAssertionClaimKind;
    }

    private static string DetermineStage(CandidateReviewFinding finding)
    {
        return string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal)
               || finding.Evidence?.SupportingFiles.Count > 1
            ? ClaimDescriptor.PrLevelStage
            : ClaimDescriptor.LocalStage;
    }

    private static string DetermineVerificationMode(string claimKind, string claimFamily)
    {
        return string.Equals(claimKind, CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind, StringComparison.Ordinal) ||
               string.Equals(claimFamily, ClaimDescriptor.ApiOrSymbolUsageFamily, StringComparison.Ordinal) ||
               string.Equals(claimFamily, ClaimDescriptor.ConfigurationOrWiringFamily, StringComparison.Ordinal) ||
               string.Equals(claimFamily, ClaimDescriptor.CrossFileConsistencyFamily, StringComparison.Ordinal) ||
               string.Equals(claimFamily, ClaimDescriptor.TestAdequacyFamily, StringComparison.Ordinal) ||
               string.Equals(claimFamily, ClaimDescriptor.DocumentationAccuracyFamily, StringComparison.Ordinal)
            ? ClaimDescriptor.NeedsEvidenceMode
            : ClaimDescriptor.DeterministicOnlyMode;
    }

    private static string DetermineClaimFamily(string claimKind, CandidateReviewFinding finding)
    {
        if (string.Equals(claimKind, CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind, StringComparison.Ordinal) ||
            string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal) ||
            string.Equals(finding.Category, "architecture", StringComparison.Ordinal) ||
            finding.Evidence?.SupportingFiles.Count > 1)
        {
            return ClaimDescriptor.CrossFileConsistencyFamily;
        }

        if (string.Equals(finding.Category, "configuration", StringComparison.Ordinal))
        {
            return ClaimDescriptor.ConfigurationOrWiringFamily;
        }

        if (string.Equals(finding.Category, "test", StringComparison.Ordinal))
        {
            return ClaimDescriptor.TestAdequacyFamily;
        }

        if (string.Equals(finding.Category, "documentation", StringComparison.Ordinal))
        {
            return ClaimDescriptor.DocumentationAccuracyFamily;
        }

        if (string.Equals(finding.Category, "robustness", StringComparison.Ordinal))
        {
            return ClaimDescriptor.OperationalRiskFamily;
        }

        if (finding.Message.Contains("symbol", StringComparison.OrdinalIgnoreCase) ||
            finding.Message.Contains("API", StringComparison.Ordinal) ||
            finding.Message.Contains("method", StringComparison.OrdinalIgnoreCase) ||
            finding.Message.Contains("function", StringComparison.OrdinalIgnoreCase) ||
            finding.Message.Contains("helper", StringComparison.OrdinalIgnoreCase) ||
            finding.Message.Contains("using ", StringComparison.OrdinalIgnoreCase) ||
            finding.Message.Contains("namespace", StringComparison.OrdinalIgnoreCase))
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

        foreach (var regex in new[] { IdentifierInBackticksRegex, NamedProgramElementRegex, MissingIdentifierRegex })
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
}
