// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>A source mutation committed atomically with a request to refresh its joint counts.</summary>
public sealed class CodeInsightPerformanceDirty
{
    public Guid Id { get; init; }
    public Guid CodeInsightPullRequestId { get; init; }
}
