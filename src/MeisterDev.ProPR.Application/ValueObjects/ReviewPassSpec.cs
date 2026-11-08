// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.ValueObjects;

/// <summary>
///     One entry in a client's ordered review-pass list as consumed by the review runtime. A pass selects its model
///     either by a named <paramref name="LogicalModelName" /> (the named role — the connection, model and protocol come
///     from the resolved logical model) or, for not-yet-migrated rows, by a concrete <paramref name="ConfiguredModelId" />.
///     <paramref name="ReasoningLevel" /> is the level of the pass; without one, the pass uses the level of its logical
///     model and then the default level of its configured model.
///     A <see langword="null" /> lens is an ordinary resample pass; a known lens value selects a specialist prompt
///     scoped to the files that lens targets. A <see langword="null" /> scope is the per-file default; <c>pr_wide</c>
///     runs the pass at the job level. <paramref name="Shadow" /> is additive metadata the runtime does not act on yet.
/// </summary>
public sealed record ReviewPassSpec(
    Guid ConfiguredModelId,
    string? Lens = null,
    string? Scope = null,
    bool Shadow = false,
    string? ReasoningLevel = null,
    string? LogicalModelName = null);
