// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
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
            return BuildUnavailableBundle(workItem);
        }

        var evidenceItems = new List<EvidenceItem>();
        var evidenceAttempts = new List<EvidenceAttemptRecord>();
        var proCursorStatuses = new List<string>();
        var attemptOrder = new int[1];

        await CollectSupportingFileEvidenceAsync(workItem, reviewTools, sourceBranch, evidenceItems, evidenceAttempts, attemptOrder, ct);

        if (workItem.Claim.RequiresCrossFileEvidence)
        {
            await CollectCrossFileEvidenceAsync(workItem, reviewTools, evidenceItems, evidenceAttempts, proCursorStatuses, attemptOrder, ct);
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

    private static EvidenceBundle BuildUnavailableBundle(VerificationWorkItem workItem)
    {
        return new EvidenceBundle(
            workItem.Claim.ClaimId,
            [],
            EvidenceBundle.MissingCoverage,
            "Review tools were unavailable for PR-level evidence collection.",
            [
                CreateAttempt(
                    workItem.Claim.ClaimId,
                    1,
                    EvidenceAttemptRecord.RepositoryStructureSource,
                    EvidenceAttemptRecord.UnavailableStatus,
                    "Review tools were unavailable for PR-level evidence collection.",
                    EvidenceAttemptRecord.ReducedConfidenceCoverageImpact,
                    failureReason: "review_tools_unavailable"),
            ]);
    }

    private static async Task CollectSupportingFileEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools reviewTools,
        string sourceBranch,
        List<EvidenceItem> evidenceItems,
        List<EvidenceAttemptRecord> evidenceAttempts,
        int[] attemptOrder,
        CancellationToken ct)
    {
        if (workItem.ExistingEvidence?.SupportingFiles.Count is not > 0)
        {
            return;
        }

        foreach (var file in workItem.ExistingEvidence.SupportingFiles.Take(3))
        {
            attemptOrder[0]++;
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
                        attemptOrder[0],
                        EvidenceAttemptRecord.FileContentSource,
                        EvidenceAttemptRecord.SucceededStatus,
                        $"Fetched supporting file '{file}' from the PR source branch.",
                        EvidenceAttemptRecord.ExpandedCoverageImpact,
                        file));
                continue;
            }

            evidenceAttempts.Add(
                CreateAttempt(
                    workItem.Claim.ClaimId,
                    attemptOrder[0],
                    EvidenceAttemptRecord.FileContentSource,
                    EvidenceAttemptRecord.EmptyStatus,
                    $"Fetched supporting file '{file}' from the PR source branch.",
                    EvidenceAttemptRecord.NoChangeCoverageImpact,
                    file,
                    "empty_file_content"));
        }
    }

    private static async Task CollectCrossFileEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools reviewTools,
        List<EvidenceItem> evidenceItems,
        List<EvidenceAttemptRecord> evidenceAttempts,
        List<string> proCursorStatuses,
        int[] attemptOrder,
        CancellationToken ct)
    {
        await CollectChangedFilesEvidenceAsync(workItem, reviewTools, evidenceItems, evidenceAttempts, attemptOrder, ct);
        await CollectProCursorKnowledgeEvidenceAsync(workItem, reviewTools, evidenceItems, evidenceAttempts, proCursorStatuses, attemptOrder, ct);

        if (workItem.Claim.RequiresSymbolEvidence && !string.IsNullOrWhiteSpace(workItem.Claim.SubjectIdentifier))
        {
            await CollectProCursorSymbolEvidenceAsync(workItem, reviewTools, evidenceItems, evidenceAttempts, proCursorStatuses, attemptOrder, ct);
        }
    }

    private static async Task CollectChangedFilesEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools reviewTools,
        List<EvidenceItem> evidenceItems,
        List<EvidenceAttemptRecord> evidenceAttempts,
        int[] attemptOrder,
        CancellationToken ct)
    {
        attemptOrder[0]++;
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
                    attemptOrder[0],
                    EvidenceAttemptRecord.RepositoryStructureSource,
                    EvidenceAttemptRecord.SucceededStatus,
                    $"Loaded {tree.Count} changed files from review context.",
                    EvidenceAttemptRecord.ExpandedCoverageImpact,
                    JsonSerializer.Serialize(tree.Select(file => file.Path).Take(10))));
            return;
        }

        evidenceAttempts.Add(
            CreateAttempt(
                workItem.Claim.ClaimId,
                attemptOrder[0],
                EvidenceAttemptRecord.RepositoryStructureSource,
                EvidenceAttemptRecord.EmptyStatus,
                "Loaded changed files from review context.",
                EvidenceAttemptRecord.NoChangeCoverageImpact,
                failureReason: "no_changed_files"));
    }

    private static async Task CollectProCursorKnowledgeEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools reviewTools,
        List<EvidenceItem> evidenceItems,
        List<EvidenceAttemptRecord> evidenceAttempts,
        List<string> proCursorStatuses,
        int[] attemptOrder,
        CancellationToken ct)
    {
        attemptOrder[0]++;
        ProCursorKnowledgeAnswerDto knowledge;
        try
        {
            knowledge = await reviewTools.AskProCursorKnowledgeAsync(workItem.Claim.AssertionText, ct);
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            knowledge = new ProCursorKnowledgeAnswerDto("unavailable", [], ex.Message);
        }

        if (string.Equals(knowledge.Status, "ok", StringComparison.OrdinalIgnoreCase) && knowledge.Results.Count > 0)
        {
            evidenceItems.Add(
                new EvidenceItem(
                    "ProCursorKnowledge",
                    "ProCursor returned repository knowledge relevant to the claim assertion.",
                    payloadReference: JsonSerializer.Serialize(knowledge.Results.Take(3))));
            var status = MapProCursorStatus(knowledge.Status, true);
            proCursorStatuses.Add(status);
            evidenceAttempts.Add(
                CreateAttempt(
                    workItem.Claim.ClaimId,
                    attemptOrder[0],
                    EvidenceAttemptRecord.ProCursorKnowledgeSource,
                    status,
                    "Queried ProCursor knowledge for the claim assertion.",
                    EvidenceAttemptRecord.ExpandedCoverageImpact,
                    JsonSerializer.Serialize(knowledge.Results.Take(3))));
            return;
        }

        var emptyStatus = MapProCursorStatus(knowledge.Status, false);
        proCursorStatuses.Add(emptyStatus);
        evidenceAttempts.Add(
            CreateAttempt(
                workItem.Claim.ClaimId,
                attemptOrder[0],
                EvidenceAttemptRecord.ProCursorKnowledgeSource,
                emptyStatus,
                "Queried ProCursor knowledge for the claim assertion.",
                emptyStatus == EvidenceAttemptRecord.UnavailableStatus
                    ? EvidenceAttemptRecord.ReducedConfidenceCoverageImpact
                    : EvidenceAttemptRecord.NoChangeCoverageImpact,
                failureReason: knowledge.NoResultReason ?? knowledge.Status));
    }

    private static async Task CollectProCursorSymbolEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools reviewTools,
        List<EvidenceItem> evidenceItems,
        List<EvidenceAttemptRecord> evidenceAttempts,
        List<string> proCursorStatuses,
        int[] attemptOrder,
        CancellationToken ct)
    {
        attemptOrder[0]++;
        ProCursorSymbolInsightDto symbol;
        try
        {
            symbol = await reviewTools.GetProCursorSymbolInfoAsync(workItem.Claim.SubjectIdentifier!, "name", 5, ct);
        }
        catch (ProCursorDependencyUnavailableException)
        {
            symbol = new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []);
        }

        if (string.Equals(symbol.Status, "ok", StringComparison.OrdinalIgnoreCase) && symbol.Symbol is not null)
        {
            evidenceItems.Add(
                new EvidenceItem(
                    "ProCursorSymbolInsight",
                    $"Resolved symbol insight for '{workItem.Claim.SubjectIdentifier}'.",
                    payloadReference: JsonSerializer.Serialize(symbol.Symbol)));
            var status = MapProCursorStatus(symbol.Status, true);
            proCursorStatuses.Add(status);
            evidenceAttempts.Add(
                CreateAttempt(
                    workItem.Claim.ClaimId,
                    attemptOrder[0],
                    EvidenceAttemptRecord.ProCursorSymbolSource,
                    status,
                    $"Queried ProCursor symbol insight for '{workItem.Claim.SubjectIdentifier}'.",
                    EvidenceAttemptRecord.ExpandedCoverageImpact,
                    JsonSerializer.Serialize(symbol.Symbol)));
            return;
        }

        var emptyStatus = MapProCursorStatus(symbol.Status, false);
        proCursorStatuses.Add(emptyStatus);
        evidenceAttempts.Add(
            CreateAttempt(
                workItem.Claim.ClaimId,
                attemptOrder[0],
                EvidenceAttemptRecord.ProCursorSymbolSource,
                emptyStatus,
                $"Queried ProCursor symbol insight for '{workItem.Claim.SubjectIdentifier}'.",
                emptyStatus == EvidenceAttemptRecord.UnavailableStatus
                    ? EvidenceAttemptRecord.ReducedConfidenceCoverageImpact
                    : EvidenceAttemptRecord.NoChangeCoverageImpact,
                failureReason: symbol.Status));
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
