// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.DTOs;

/// <summary>Normalized reference to one assigned code review discovered during crawl.</summary>
public sealed record AssignedCodeReviewRef(
    ProviderHostRef Host,
    RepositoryRef Repository,
    CodeReviewRef CodeReview,
    int RevisionId,
    string? ReviewTitle = null,
    string? RepositoryDisplayName = null,
    string? SourceBranch = null,
    string? TargetBranch = null,
    ReviewRevision? ReviewRevision = null);
