// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>Latest source eligibility for a thread, independent of any completed human judgement.</summary>
public sealed class CodeInsightThreadEligibility
{
    public Guid Id { get; init; }
    public Guid CodeInsightPullRequestId { get; init; }
    public CodeInsightPullRequest? CodeInsightPullRequest { get; init; }
    public string ProviderScope { get; init; } = string.Empty;
    public string ProviderThreadId { get; init; } = string.Empty;
    public string IdentityFingerprint { get; init; } = string.Empty;
    public DateTimeOffset SourceObservedAt { get; set; }
    public bool ExcludedFromHumanMisses { get; set; }
}
