// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Provider-safe rendered review text plus the transformations needed to produce it.
/// </summary>
public sealed record RenderedReviewBody(
    string OriginalText,
    string RenderedText,
    ReviewBodyRenderingMode RenderingMode,
    IReadOnlyList<string> SafetyTransformations,
    bool ContainsUnsafeMarkup);

/// <summary>
///     Publication surface the rendered body targets.
/// </summary>
public enum ReviewBodyRenderingMode
{
    Summary,
    InlineComment,
    ThreadReply,
}
