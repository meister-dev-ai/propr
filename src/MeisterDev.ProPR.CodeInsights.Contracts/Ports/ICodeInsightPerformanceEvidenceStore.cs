// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Receipt identity observed before current-outcome processing.</summary>
public sealed record CodeInsightPublicationReceipt(string ProviderScope, string? ThreadId, string? CommentId);

/// <summary>Records current analytics evidence independently of immutable initial dispositions.</summary>
public interface ICodeInsightPerformanceEvidenceStore
{
    /// <summary>Advances an unchanged, completed outcome observation under the same receipt-family lock.</summary>
    /// <returns>True when the observation matches retained completed evidence or is older than its watermark; false when current evidence needs processing.</returns>
    Task<bool> ObserveUnchangedOutcomeAsync(
        Guid findingId, string nativeStatus, string sourceFingerprint, DateTimeOffset observedAt, CancellationToken ct = default,
        CodeInsightPublicationReceipt? receipt = null);

    /// <summary>Replaces current outcome evidence, including a reopened thread without a verdict.</summary>
    Task<bool> RecordCurrentOutcomeAsync(
        Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct = default,
        string? sourceFingerprint = null, CodeInsightPublicationReceipt? receipt = null);
}
