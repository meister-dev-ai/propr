// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Specifies a repository-name filter for a crawl configuration.
///     When <see cref="TargetBranchPatterns" /> is empty all branches are accepted.
/// </summary>
/// <param name="RepositoryName">Plain display name of the ADO repository (case-insensitive match).</param>
/// <param name="TargetBranchPatterns">
///     Glob patterns (supporting <c>*</c> wildcard) to match against the PR target branch.
///     Empty list means all branches for this repository are accepted.
/// </param>
/// <param name="CanonicalSourceRef">Provider-aware canonical source reference for guided repository selection.</param>
/// <param name="DisplayName">Human-readable repository display snapshot used by guided configuration.</param>
public sealed record CrawlRepoFilter(
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReference? CanonicalSourceRef = null,
    string? DisplayName = null);
