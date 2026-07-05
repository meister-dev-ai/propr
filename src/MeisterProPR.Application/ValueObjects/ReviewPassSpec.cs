// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     One entry in a client's ordered review-pass list as consumed by the review runtime: the configured model that
///     runs the pass (its connection implied) and an optional specialist lens. A <see langword="null" /> lens is an
///     ordinary resample pass; a known lens value selects a specialist prompt scoped to the files that lens targets.
/// </summary>
public sealed record ReviewPassSpec(Guid ConfiguredModelId, string? Lens = null);
