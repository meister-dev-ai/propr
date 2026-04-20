// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Normalized request used to create review-context tools for one code review execution.</summary>
public sealed record ReviewContextToolsRequest(
    CodeReviewRef CodeReview,
    string SourceBranch,
    int IterationId,
    Guid? ClientId,
    IReadOnlyList<Guid>? KnowledgeSourceIds = null,
    string? ProviderScopePath = null);
