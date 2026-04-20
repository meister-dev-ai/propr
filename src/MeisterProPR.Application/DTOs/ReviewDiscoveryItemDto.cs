// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.DTOs;

/// <summary>Normalized review discovery item surfaced from a provider adapter.</summary>
public sealed record ReviewDiscoveryItemDto(
    ScmProvider Provider,
    RepositoryRef Repository,
    CodeReviewRef CodeReview,
    CodeReviewState ReviewState,
    ReviewRevision? ReviewRevision,
    ReviewerIdentity? RequestedReviewerIdentity,
    string Title,
    string? WebUrl,
    string? SourceBranch,
    string? TargetBranch);
