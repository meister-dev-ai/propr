// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Records completion of a full provider thread enumeration, including an empty enumeration.</summary>
public interface ICodeInsightHarvestCoverageRecorder
{
    Task RecordAsync(
        CodeInsightPullRequestKey key, string providerScope, bool allHumanThreadsResolved, DateTimeOffset observedAt, CancellationToken ct = default,
        bool enumerationComplete = true);
}
