// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Comment messages and native discussion resolution metadata for one pull request.</summary>
/// <param name="TotalComments">Published user messages from all authors; null when the count is unavailable or incomplete.</param>
/// <param name="ResolvedDiscussions">Native resolved discussions, not resolved messages; null when unavailable or unsupported.</param>
/// <param name="UnresolvedDiscussions">Native unresolved discussions, not unresolved messages; null when unavailable or unsupported.</param>
/// <param name="IsComplete">Whether the provider read completed within its bounds.</param>
/// <param name="ResolutionSupported">Whether the registered provider capability exposes discussion resolution.</param>
public sealed record ReviewOverviewDto(
    int? TotalComments,
    int? ResolvedDiscussions,
    int? UnresolvedDiscussions,
    bool IsComplete,
    bool ResolutionSupported);
