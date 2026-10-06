// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Passive collection of successful, scoped model execution evidence.</summary>
public interface ICodeInsightReviewExposureCollector
{
    /// <summary>Records execution identity without inferring missed-issue attribution.</summary>
    Task RecordAsync(
        CodeInsightPullRequestKey key, Guid jobId, string filePath, string revisionKey,
        string modelId, string? logicalModelName, string providerScope, string source, DateTimeOffset observedAt, CancellationToken ct = default);
}
