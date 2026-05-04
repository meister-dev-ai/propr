// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Collects bounded PR-level verification evidence through the existing review-tools boundary.
/// </summary>
public sealed class ReviewContextEvidenceCollector : IReviewEvidenceCollector
{
    public async Task<EvidenceBundle> CollectEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools? reviewTools,
        string sourceBranch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (reviewTools is null)
        {
            return new EvidenceBundle(
                workItem.Claim.ClaimId,
                [],
                EvidenceBundle.MissingCoverage,
                "Review tools were unavailable for PR-level evidence collection.",
                [CreateAttempt(
                    workItem.Claim.ClaimId,
                    1,
                    EvidenceAttemptRecord.RepositoryStructureSource,
                    EvidenceAttemptRecord.UnavailableStatus,
                    "Review tools were unavailable for PR-level evidence collection.",
                    EvidenceAttemptRecord.ReducedConfidenceCoverageImpact,
                    failureReason: "review_tools_unavailable")]);
        }

        var evidenceItems = new List<EvidenceItem>();
        var evidenceAttempts = new List<EvidenceAttemptRecord>();
        var attemptOrder = 0;
        var proCursorStatuses = new List<string>();

        if (workItem.ExistingEvidence?.SupportingFiles.Count > 0)
        {
            foreach (var file in workItem.ExistingEvidence.SupportingFiles.Take(3))
            {
                attemptOrder++;
                var content = await reviewTools.GetFileContentAsync(file, sourceBranch, 1, 120, ct);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    evidenceItems.Add(
                        new EvidenceItem(
                            "FileContentRange",
                            $"Fetched supporting file '{file}' from the PR source branch.",
                            file,
                            content.Length > 400 ? content[..400] : content));
                    evidenceAttempts.Add(
                        CreateAttempt(
                            workItem.Claim.ClaimId,
                            attemptOrder,
                            EvidenceAttemptRecord.FileContentSource,
                            EvidenceAttemptRecord.SucceededStatus,
                            $"Fetched supporting file '{file}' from the PR source branch.",
                            EvidenceAttemptRecord.ExpandedCoverageImpact,
                            payloadReference: file));
                    continue;
                }

                evidenceAttempts.Add(
                    CreateAttempt(
                        workItem.Claim.ClaimId,
                        attemptOrder,
                        EvidenceAttemptRecord.FileContentSource,
                        EvidenceAttemptRecord.EmptyStatus,
                        $"Fetched supporting file '{file}' from the PR source branch.",
                        EvidenceAttemptRecord.NoChangeCoverageImpact,
                        payloadReference: file,
                        failureReason: "empty_file_content"));
            }
        }

        if (workItem.Claim.RequiresCrossFileEvidence)
        {
            attemptOrder++;
            var tree = await reviewTools.GetChangedFilesAsync(ct);
            if (tree.Count > 0)
            {
                evidenceItems.Add(
                    new EvidenceItem(
                        "RepositoryStructure",
                        $"Loaded {tree.Count} changed files from review context.",
                        payloadReference: JsonSerializer.Serialize(tree.Select(file => file.Path).Take(10))));
                evidenceAttempts.Add(
                    CreateAttempt(
                        workItem.Claim.ClaimId,
                        attemptOrder,
                        EvidenceAttemptRecord.RepositoryStructureSource,
                        EvidenceAttemptRecord.SucceededStatus,
                        $"Loaded {tree.Count} changed files from review context.",
                        EvidenceAttemptRecord.ExpandedCoverageImpact,
                        payloadReference: JsonSerializer.Serialize(tree.Select(file => file.Path).Take(10))));
            }
            else
            {
                evidenceAttempts.Add(
                    CreateAttempt(
                        workItem.Claim.ClaimId,
                        attemptOrder,
                        EvidenceAttemptRecord.RepositoryStructureSource,
                        EvidenceAttemptRecord.EmptyStatus,
                        "Loaded changed files from review context.",
                        EvidenceAttemptRecord.NoChangeCoverageImpact,
                        failureReason: "no_changed_files"));
            }

            attemptOrder++;
            var knowledge = await reviewTools.AskProCursorKnowledgeAsync(workItem.Claim.AssertionText, ct);
            if (string.Equals(knowledge.Status, "ok", StringComparison.OrdinalIgnoreCase) && knowledge.Results.Count > 0)
            {
                evidenceItems.Add(
                    new EvidenceItem(
                        "ProCursorKnowledge",
                        "ProCursor returned repository knowledge relevant to the claim assertion.",
                        payloadReference: JsonSerializer.Serialize(knowledge.Results.Take(3))));
                var status = MapProCursorStatus(knowledge.Status, hasResults: true);
                proCursorStatuses.Add(status);
                evidenceAttempts.Add(
                    CreateAttempt(
                        workItem.Claim.ClaimId,
                        attemptOrder,
                        EvidenceAttemptRecord.ProCursorKnowledgeSource,
                        status,
                        "Queried ProCursor knowledge for the claim assertion.",
                        EvidenceAttemptRecord.ExpandedCoverageImpact,
                        payloadReference: JsonSerializer.Serialize(knowledge.Results.Take(3))));
            }
            else
            {
                var status = MapProCursorStatus(knowledge.Status, hasResults: false);
                proCursorStatuses.Add(status);
                evidenceAttempts.Add(
                    CreateAttempt(
                        workItem.Claim.ClaimId,
                        attemptOrder,
                        EvidenceAttemptRecord.ProCursorKnowledgeSource,
                        status,
                        "Queried ProCursor knowledge for the claim assertion.",
                        status == EvidenceAttemptRecord.UnavailableStatus
                            ? EvidenceAttemptRecord.ReducedConfidenceCoverageImpact
                            : EvidenceAttemptRecord.NoChangeCoverageImpact,
                        failureReason: knowledge.NoResultReason ?? knowledge.Status));
            }

            if (workItem.Claim.RequiresSymbolEvidence && !string.IsNullOrWhiteSpace(workItem.Claim.SubjectIdentifier))
            {
                attemptOrder++;
                var symbol = await reviewTools.GetProCursorSymbolInfoAsync(workItem.Claim.SubjectIdentifier, "name", 5, ct);
                if (string.Equals(symbol.Status, "ok", StringComparison.OrdinalIgnoreCase) && symbol.Symbol is not null)
                {
                    evidenceItems.Add(
                        new EvidenceItem(
                            "ProCursorSymbolInsight",
                            $"Resolved symbol insight for '{workItem.Claim.SubjectIdentifier}'.",
                            payloadReference: JsonSerializer.Serialize(symbol.Symbol)));
                    var status = MapProCursorStatus(symbol.Status, hasResults: true);
                    proCursorStatuses.Add(status);
                    evidenceAttempts.Add(
                        CreateAttempt(
                            workItem.Claim.ClaimId,
                            attemptOrder,
                            EvidenceAttemptRecord.ProCursorSymbolSource,
                            status,
                            $"Queried ProCursor symbol insight for '{workItem.Claim.SubjectIdentifier}'.",
                            EvidenceAttemptRecord.ExpandedCoverageImpact,
                            payloadReference: JsonSerializer.Serialize(symbol.Symbol)));
                }
                else
                {
                    var status = MapProCursorStatus(symbol.Status, hasResults: false);
                    proCursorStatuses.Add(status);
                    evidenceAttempts.Add(
                        CreateAttempt(
                            workItem.Claim.ClaimId,
                            attemptOrder,
                            EvidenceAttemptRecord.ProCursorSymbolSource,
                            status,
                            $"Queried ProCursor symbol insight for '{workItem.Claim.SubjectIdentifier}'.",
                            status == EvidenceAttemptRecord.UnavailableStatus
                                ? EvidenceAttemptRecord.ReducedConfidenceCoverageImpact
                                : EvidenceAttemptRecord.NoChangeCoverageImpact,
                            failureReason: symbol.Status));
                }
            }
        }

        var coverage = evidenceItems.Count switch
        {
            0 => EvidenceBundle.MissingCoverage,
            1 => EvidenceBundle.PartialCoverage,
            _ => EvidenceBundle.CompleteCoverage,
        };

        return new EvidenceBundle(
            workItem.Claim.ClaimId,
            evidenceItems,
            coverage,
            coverage == EvidenceBundle.MissingCoverage ? "No independent PR-level evidence was collected." : null,
            evidenceAttempts,
            proCursorStatuses.Count > 0,
            ResolveAggregateProCursorStatus(proCursorStatuses));
    }

    private static EvidenceAttemptRecord CreateAttempt(
        string claimId,
        int order,
        string sourceFamily,
        string status,
        string scopeSummary,
        string coverageImpact,
        string? payloadReference = null,
        string? failureReason = null)
    {
        return new EvidenceAttemptRecord(
            $"{claimId}:attempt:{order:D3}",
            claimId,
            sourceFamily,
            order,
            status,
            scopeSummary,
            coverageImpact,
            payloadReference,
            failureReason);
    }

    private static string MapProCursorStatus(string status, bool hasResults)
    {
        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return hasResults ? EvidenceAttemptRecord.SucceededStatus : EvidenceAttemptRecord.EmptyStatus;
        }

        if (string.Equals(status, "no_result", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "notFound", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "unsupportedLanguage", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceAttemptRecord.EmptyStatus;
        }

        return EvidenceAttemptRecord.UnavailableStatus;
    }

    private static string? ResolveAggregateProCursorStatus(IReadOnlyList<string> statuses)
    {
        if (statuses.Count == 0)
        {
            return null;
        }

        if (statuses.Any(status => string.Equals(status, EvidenceAttemptRecord.SucceededStatus, StringComparison.Ordinal)))
        {
            return EvidenceAttemptRecord.SucceededStatus;
        }

        if (statuses.Any(status => string.Equals(status, EvidenceAttemptRecord.UnavailableStatus, StringComparison.Ordinal)))
        {
            return EvidenceAttemptRecord.UnavailableStatus;
        }

        if (statuses.Any(status => string.Equals(status, EvidenceAttemptRecord.EmptyStatus, StringComparison.Ordinal)))
        {
            return EvidenceAttemptRecord.EmptyStatus;
        }

        return statuses[0];
    }
}
