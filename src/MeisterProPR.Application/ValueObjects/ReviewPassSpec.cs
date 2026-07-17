// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     One entry in a client's ordered review-pass list as consumed by the review runtime: the configured model that
///     runs the pass (its connection implied), an optional specialist lens, an optional scope, a shadow flag, and the
///     reasoning effort the pass asks the model to spend. A <see langword="null" /> lens is an ordinary resample pass;
///     a known lens value selects a specialist prompt scoped to the files that lens targets. A <see langword="null" />
///     scope is the per-file default; <c>pr_wide</c> runs the pass at the job level. <paramref name="Shadow" /> is
///     additive metadata the runtime does not act on yet. <paramref name="ReasoningEffort" /> defaults to
///     <see cref="ReviewReasoningEffort.None" /> (no effort sent — current behavior).
/// </summary>
public sealed record ReviewPassSpec(
    Guid ConfiguredModelId,
    string? Lens = null,
    string? Scope = null,
    bool Shadow = false,
    ReviewReasoningEffort ReasoningEffort = ReviewReasoningEffort.None);
