// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Rollups;

internal sealed record ReviewerPerformanceDispositionEvidence(
    Guid CodeInsightFindingId,
    CodeInsightDisposition Disposition,
    string? NativeStatus,
    string? ClassifierVersion,
    double? ClassifierConfidence);

internal sealed record ReviewerPerformanceTagEvidence(Guid CodeInsightFindingId, string? CoreSlug);

internal sealed record ReviewerPerformanceExposureEvidence(DateTimeOffset ObservedAt, string ProviderScope);

internal sealed record ReviewerPerformanceFindingEvidence(
    Guid Id,
    DateTimeOffset ObservedAt,
    string? OriginModelId,
    string? OriginLogicalModelName,
    CodeInsightFindingQualifier? Qualifier,
    DateTimeOffset? ClassifiedAt,
    string ProviderScope,
    string? ProviderCommentId,
    string? ProviderThreadId,
    CodeInsightPublicationState PublicationState,
    CodeInsightDuplicateState DuplicateState,
    string? NativeStatus,
    CodeInsightDisposition? CurrentDisposition,
    string? CurrentClassifierVersion,
    double? CurrentClassifierConfidence);

internal sealed record ReviewerPerformanceMissEvidence(
    DateTimeOffset HarvestedAt,
    bool IsSubstantive,
    bool IsInScope,
    bool WasActedOn,
    bool JudgedThreadResolved,
    string TypeMembership,
    CodeInsightFindingQualifier? Qualifier,
    bool JudgementFailed,
    string? DimensionClassifierVersion,
    bool DimensionJudgementFailed,
    string ProviderScope,
    bool ExcludedAsOwnFinding);

internal sealed record ReviewerPerformanceProjectionEvidence(
    List<ReviewerPerformanceFindingEvidence> Findings,
    Dictionary<Guid, ReviewerPerformanceDispositionEvidence> Dispositions,
    Dictionary<Guid, string> Memberships,
    List<ReviewerPerformanceMissEvidence> Misses,
    List<ReviewerPerformanceExposureEvidence> Exposures,
    List<CodeInsightHarvestCoverage> Coverage);
