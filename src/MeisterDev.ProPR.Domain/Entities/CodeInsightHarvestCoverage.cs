// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>Latest full thread enumeration and retention result for one provider namespace.</summary>
public sealed class CodeInsightHarvestCoverage
{
    public Guid Id { get; init; }
    public Guid CodeInsightPullRequestId { get; init; }
    public string ProviderScope { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public bool AllHumanThreadsResolved { get; set; }
    public bool EnumerationComplete { get; set; }
}
