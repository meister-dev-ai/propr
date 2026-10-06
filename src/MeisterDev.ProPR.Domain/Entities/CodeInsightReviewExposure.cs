// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>Recorded successful model execution on a concrete review scope, including zero-finding reviews.</summary>
public sealed class CodeInsightReviewExposure
{
    public Guid Id { get; set; }
    public Guid CodeInsightPullRequestId { get; set; }
    public Guid JobId { get; set; }

    /// <summary>Database-generated canonical identity digest for bounded uniqueness.</summary>
    public string IdentityFingerprint { get; private set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;
    public string RevisionKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string LogicalModelName { get; set; } = string.Empty;
    public string ProviderScope { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
}
