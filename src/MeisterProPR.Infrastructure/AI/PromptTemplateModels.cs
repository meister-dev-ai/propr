// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.AI;

internal static class PromptTemplateModels
{
    internal sealed record SynthesisSystemModel(bool jsonMode);

    internal sealed record AgenticFilePlanningSystemModel(string? focusedReviewGuidanceSection);

    internal sealed record AgenticFilePlanningUserModel(
        string prTitle,
        string sourceBranch,
        string targetBranch,
        string anchorFilePath,
        string? description,
        IReadOnlyList<PromptFileManifestItem> manifestItems,
        string anchorFileDiff);

    internal sealed record AgenticFileInvestigationUserModel(
        string planId,
        string anchorFilePath,
        string taskId,
        string taskType,
        string concern,
        string sourceBranch,
        string allowedTools,
        int maxToolCalls,
        IReadOnlyList<PromptFileManifestItem> manifestItems);

    internal sealed record PerFileContextModel(
        string filePath,
        int fileIndex,
        int totalFiles,
        bool multipleFiles,
        string? focusedReviewGuidanceSection,
        string? agenticPlanSection,
        bool hasInvestigations,
        IReadOnlyList<PromptInvestigationResultModel> investigations,
        string outputKeyReminder);

    internal sealed record PerFileUserModel(
        string filePath,
        string changeType,
        int fileIndex,
        int totalFiles,
        string prTitle,
        string sourceBranch,
        string targetBranch,
        bool isReReviewPass,
        IReadOnlyList<PromptFileManifestItem> manifest,
        bool isBinary,
        string diffText,
        bool hasThreads,
        IReadOnlyList<PromptThreadModel> threads);

    internal sealed record SynthesisUserModel(
        string prTitle,
        string? prDescription,
        IReadOnlyList<PromptSummaryItem> summaries,
        bool hasFindings,
        bool hasCandidateFindings,
        IReadOnlyList<PromptCandidateFindingItem> candidateFindings,
        IReadOnlyList<PromptCommentItem> comments,
        string findingIdInstructionSuffix,
        bool jsonResponseRequested);

    internal sealed record PrVerificationUserModel(
        string claimId,
        string findingId,
        string claimKind,
        string claimFamily,
        string assertionText,
        string coverageState,
        bool hasProCursorAttempt,
        string? proCursorResultStatus,
        string? retrievalNotes,
        bool hasEvidenceItems,
        IReadOnlyList<PromptEvidenceItemModel> evidenceItems,
        bool hasEvidenceAttempts,
        IReadOnlyList<PromptEvidenceAttemptModel> evidenceAttempts);

    internal sealed record PrWidePlanningSystemModel;

    internal sealed record PrWidePlanningUserModel(
        string prTitle,
        string sourceBranch,
        string targetBranch,
        int changedFileCount,
        string? description,
        IReadOnlyList<PromptFileManifestItem> manifestItems,
        IReadOnlyList<PromptDiffExcerptItem> diffExcerpts);

    internal sealed record PrWideInvestigationSystemModel;

    internal sealed record PrWideInvestigationUserModel(
        string planId,
        string taskId,
        string taskType,
        string concern,
        string sourceBranch,
        string allowedTools,
        int maxToolCalls,
        IReadOnlyList<PromptFileManifestItem> manifestItems);

    internal sealed record PrWideSynthesisSystemModel;

    internal sealed record PrWideSynthesisUserModel(
        string planId,
        IReadOnlyList<string> concerns,
        IReadOnlyList<PromptInvestigationResultModel> investigations);

    internal sealed record PromptFileManifestItem(string path, string changeType, bool isCurrentFile, bool isAnchorFile);

    internal sealed record PromptDiffExcerptItem(string path, string diffText);

    internal sealed record PromptThreadModel(string location, IReadOnlyList<PromptThreadCommentModel> comments);

    internal sealed record PromptThreadCommentModel(string authorName, string content);

    internal sealed record PromptSummaryItem(string filePath, string summary);

    internal sealed record PromptCommentItem(string filePath, string severity, string message);

    internal sealed record PromptCandidateFindingItem(string findingId, string filePath, string severity, string message);

    internal sealed record PromptEvidenceItemModel(string kind, string? sourceId, string summary, string? payloadReference);

    internal sealed record PromptEvidenceAttemptModel(string sourceFamily, string status, string coverageImpact, string scopeSummary, string? failureReason);

    internal sealed record PromptInvestigationResultModel(
        string taskId,
        string status,
        bool hasEvidence,
        IReadOnlyList<PromptSimpleEvidenceItemModel> evidence,
        bool hasCandidateFindings,
        IReadOnlyList<PromptPrWideCandidateFindingModel> candidateFindings);

    internal sealed record PromptSimpleEvidenceItemModel(string kind, string summary, string sourceId);

    internal sealed record PromptPrWideCandidateFindingModel(string id, string message, string category, string confidence, string supportingFiles);
}
