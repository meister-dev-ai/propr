// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>One joint daily count cell. Type membership is retained as a set for union deduplication.</summary>
public sealed class ReviewerPerformanceDailyCount
{
    /// <summary>Assessed consequence, or Unknown when no assessment was retained.</summary>
    public FindingImpact Impact { get; set; }

    /// <summary>Occurrence likelihood independent of correctness confidence.</summary>
    public FindingLikelihood Likelihood { get; set; }

    /// <summary>Strength of correctness evidence, or Unknown for unassessed findings.</summary>
    public FindingConfidence Confidence { get; set; }

    /// <summary>Server-derived risk severity.</summary>
    public FindingRiskSeverity RiskSeverity { get; set; }

    /// <summary>Rubric used for the retained assessment, or empty for historical findings.</summary>
    public string RiskRubricVersion { get; set; } = string.Empty;


    public Guid Id { get; set; }
    public string CellKey { get; set; } = string.Empty;
    public Guid CodeInsightPullRequestId { get; set; }
    public Guid ClientId { get; set; }
    public string RepositoryId { get; set; } = string.Empty;
    public string ProviderScope { get; set; } = string.Empty;
    public long PullRequestId { get; set; }
    public DateOnly BucketDate { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string LogicalModelName { get; set; } = string.Empty;
    public string TypeMembership { get; set; } = string.Empty;
    public string Qualifier { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public CodeInsightPublicationState PublicationState { get; set; }
    public CodeInsightDuplicateState DuplicateState { get; set; }
    public bool IsMiss { get; set; }
    public bool IsClassified { get; set; }
    public long Count { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
