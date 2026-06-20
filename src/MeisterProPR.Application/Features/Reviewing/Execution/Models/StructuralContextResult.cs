// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The boundary-resolved context the prefetch seam consumes. Shape-compatible with the
///     tuple returned by <c>BuildSurroundingContext</c> today
///     (<c>surroundingContent, surroundingTruncated, windowedInjection, windowCount, firstWindowStartLine</c>),
///     extended with <see cref="BoundaryResolved" />/<see cref="EnclosingSymbol" />/
///     <see cref="EnclosingKind" />/<see cref="FallbackReason" /> for the trace.
/// </summary>
public sealed record StructuralContextResult(
    string RenderedContent,
    bool Truncated,
    bool BoundaryResolved,
    int WindowCount,
    int? FirstWindowStartLine,
    string? EnclosingSymbol,
    DefinitionKind? EnclosingKind,
    FallbackReason? FallbackReason);
