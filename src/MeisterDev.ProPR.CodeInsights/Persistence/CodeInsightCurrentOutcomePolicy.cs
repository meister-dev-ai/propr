// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

internal static class CodeInsightCurrentOutcomePolicy
{
    private const int MaximumFailedJudgementAttempts = 3;

    internal static bool MatchesReceipt(CodeInsightFinding finding, CodeInsightPublicationReceipt? receipt)
    {
        return receipt is null || finding.ProviderScope == receipt.ProviderScope
            && finding.ProviderThreadId == receipt.ThreadId
            && finding.ProviderCommentId == receipt.CommentId;
    }

    internal static bool MatchesOutcome(CodeInsightFinding finding, string nativeStatus, CodeInsightDispositionRecord? outcome, string? sourceFingerprint)
    {
        return string.Equals(finding.NativeStatus, nativeStatus, StringComparison.OrdinalIgnoreCase)
               && finding.CurrentDisposition == outcome?.Disposition
               && finding.CurrentClassifierVersion == outcome?.ClassifierVersion
               && finding.CurrentClassifierConfidence == outcome?.ClassifierConfidence
               && finding.OutcomeSourceFingerprint == sourceFingerprint
               && finding.CurrentCodeChange == (outcome?.SourceCodeChange ?? ThreadAnchorCodeChange.Unknown)
               && (outcome?.ClassifierVersion is null || outcome.ClassifierConfidence is not null
                                                      || finding.OutcomeJudgementAttempts >= MaximumFailedJudgementAttempts);
    }

    internal static bool MatchesCompleted(CodeInsightFinding row, string nativeStatus, string sourceFingerprint)
    {
        return string.Equals(row.NativeStatus, nativeStatus, StringComparison.OrdinalIgnoreCase)
               && row.OutcomeSourceFingerprint == sourceFingerprint
               && (row.CurrentClassifierVersion is null || row.CurrentClassifierConfidence is not null
                                                        || row.OutcomeJudgementAttempts >= MaximumFailedJudgementAttempts);
    }

    internal static void Apply(
        CodeInsightFinding publication, string nativeStatus, CodeInsightDispositionRecord? outcome,
        DateTimeOffset observedAt, string? sourceFingerprint)
    {
        var sameSource = publication.NativeStatus == nativeStatus
                         && publication.CurrentCodeChange == outcome?.SourceCodeChange
                         && publication.OutcomeSourceFingerprint == sourceFingerprint
                         && publication.CurrentClassifierVersion == outcome?.ClassifierVersion;
        publication.OutcomeJudgementAttempts = outcome?.ClassifierVersion is not null && outcome.ClassifierConfidence is null
            ? (sameSource ? publication.OutcomeJudgementAttempts : 0) + 1
            : 0;
        publication.NativeStatus = nativeStatus;
        publication.CurrentDisposition = outcome?.Disposition;
        publication.CurrentClassifierVersion = outcome?.ClassifierVersion;
        publication.CurrentClassifierConfidence = outcome?.ClassifierConfidence;
        publication.CurrentCodeChange = outcome?.SourceCodeChange ?? ThreadAnchorCodeChange.Unknown;
        publication.OutcomeObservedAt = observedAt;
        publication.OutcomeSourceFingerprint = sourceFingerprint;
        publication.PerformanceEvidenceUpdatedAt = DateTimeOffset.UtcNow;
    }
}
