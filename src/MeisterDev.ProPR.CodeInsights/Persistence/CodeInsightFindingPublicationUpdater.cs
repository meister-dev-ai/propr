// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

internal static class CodeInsightFindingPublicationUpdater
{
    internal static void Apply(MeisterProPRDbContext db, CodeInsightFinding current, CodeInsightFindingSnapshot finding, DateTimeOffset now)
    {
        var receiptBefore = (current.ProviderThreadId, current.ProviderCommentId, current.ProviderScope, current.PublicationState,
            current.PublicationReason, current.MatchedProviderThreadId);
        var duplicateBefore = (current.DuplicateState, current.DuplicateOfPublicationId, current.DuplicateVerificationSource,
            current.DuplicateVerificationConfidence, current.DuplicateVerifiedAt);
        MergeReceipt(current, finding);
        if ((receiptBefore.ProviderScope, receiptBefore.ProviderThreadId, receiptBefore.ProviderCommentId) !=
            (current.ProviderScope, current.ProviderThreadId, current.ProviderCommentId))
        {
            ResetCurrentEvidence(db, current, finding);
        }

        UpdatePublicationState(current, finding);
        UpdateDuplicateState(current, finding);

        if (receiptBefore != (current.ProviderThreadId, current.ProviderCommentId, current.ProviderScope, current.PublicationState,
                current.PublicationReason, current.MatchedProviderThreadId)
            || duplicateBefore != (current.DuplicateState, current.DuplicateOfPublicationId, current.DuplicateVerificationSource,
                current.DuplicateVerificationConfidence, current.DuplicateVerifiedAt))
        {
            current.PerformanceEvidenceUpdatedAt = now;
        }
    }

    private static void MergeReceipt(CodeInsightFinding current, CodeInsightFindingSnapshot finding)
    {
        if (!string.IsNullOrEmpty(finding.ProviderThreadId))
        {
            current.ProviderThreadId = finding.ProviderThreadId;
        }

        if (!string.IsNullOrEmpty(finding.ProviderCommentId))
        {
            current.ProviderCommentId = finding.ProviderCommentId;
        }

        if (current.ProviderScope.Length == 0 && finding.ProviderScope.Length > 0)
        {
            current.ProviderScope = finding.ProviderScope;
        }
    }

    private static void ResetCurrentEvidence(MeisterProPRDbContext db, CodeInsightFinding current, CodeInsightFindingSnapshot finding)
    {
        current.NativeStatus = "Unknown";
        current.CurrentDisposition = null;
        current.CurrentClassifierVersion = null;
        current.CurrentClassifierConfidence = null;
        current.CurrentCodeChange = ThreadAnchorCodeChange.Unknown;
        current.OutcomeSourceFingerprint = null;
        current.OutcomeObservedAt = null;
        current.OutcomeJudgementAttempts = 0;
        current.DuplicateState = CodeInsightDuplicateState.Unknown;
        current.DuplicateOfPublicationId = null;
        current.DuplicateVerificationSource = null;
        current.DuplicateVerificationConfidence = null;
        current.DuplicateVerifiedAt = null;
        current.PublicationReason = finding.PublicationReason;
        current.MatchedProviderThreadId = finding.MatchedProviderThreadId;
        // Reset every current field even when its tracked original value was empty.
        foreach (var property in new[]
                 {
                     nameof(CodeInsightFinding.NativeStatus), nameof(CodeInsightFinding.CurrentDisposition),
                     nameof(CodeInsightFinding.CurrentClassifierVersion), nameof(CodeInsightFinding.CurrentClassifierConfidence),
                     nameof(CodeInsightFinding.CurrentCodeChange), nameof(CodeInsightFinding.OutcomeSourceFingerprint),
                     nameof(CodeInsightFinding.OutcomeObservedAt), nameof(CodeInsightFinding.OutcomeJudgementAttempts),
                     nameof(CodeInsightFinding.DuplicateState), nameof(CodeInsightFinding.DuplicateOfPublicationId),
                     nameof(CodeInsightFinding.DuplicateVerificationSource), nameof(CodeInsightFinding.DuplicateVerificationConfidence),
                     nameof(CodeInsightFinding.DuplicateVerifiedAt), nameof(CodeInsightFinding.PublicationReason),
                     nameof(CodeInsightFinding.MatchedProviderThreadId)
                 })
        {
            db.Entry(current).Property(property).IsModified = true;
        }
    }

    private static void UpdatePublicationState(CodeInsightFinding current, CodeInsightFindingSnapshot finding)
    {
        if (finding.PublicationState != CodeInsightPublicationState.Unknown &&
            current.PublicationState != CodeInsightPublicationState.Published)
        {
            current.PublicationState = finding.PublicationState;
            current.PublicationReason = finding.PublicationReason ?? current.PublicationReason;
            current.MatchedProviderThreadId = finding.MatchedProviderThreadId ?? current.MatchedProviderThreadId;
        }
    }

    private static void UpdateDuplicateState(CodeInsightFinding current, CodeInsightFindingSnapshot finding)
    {
        if (finding.DuplicateState != CodeInsightDuplicateState.Unknown &&
            (current.DuplicateVerifiedAt is null || finding.DuplicateVerifiedAt >= current.DuplicateVerifiedAt))
        {
            current.DuplicateState = finding.DuplicateState;
            current.DuplicateOfPublicationId = finding.DuplicateOfPublicationId ?? current.DuplicateOfPublicationId;
            current.DuplicateVerificationSource = finding.DuplicateVerificationSource ?? current.DuplicateVerificationSource;
            current.DuplicateVerificationConfidence = finding.DuplicateVerificationConfidence ?? current.DuplicateVerificationConfidence;
            current.DuplicateVerifiedAt = finding.DuplicateVerifiedAt ?? current.DuplicateVerifiedAt;
        }
    }
}
