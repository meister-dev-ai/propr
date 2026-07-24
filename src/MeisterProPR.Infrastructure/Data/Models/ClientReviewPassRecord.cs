// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF persistence model for one entry in a client's ordered review-pass list. Each entry binds one additional
///     multi-pass union pass (after the implicit tier baseline) to a configured model, at the given ordinal. An
///     optional lens selects a specialist prompt/scope for the pass (e.g. security) instead of a plain resample.
/// </summary>
public sealed class ClientReviewPassRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public int Ordinal { get; set; }

    /// <summary>
    ///     The concrete configured model that runs the pass, for not-yet-migrated rows. <see langword="null" /> when the
    ///     pass instead names a <see cref="LogicalModelName" /> (the named role). Exactly one of the two is set.
    /// </summary>
    public Guid? ConfiguredModelId { get; set; }

    /// <summary>
    ///     The named logical model that runs the pass (connection, model, reasoning effort, and protocol come from it).
    ///     <see langword="null" /> for legacy rows that still bind a concrete <see cref="ConfiguredModelId" />.
    /// </summary>
    public string? LogicalModelName { get; set; }

    /// <summary>
    ///     Optional lens for this pass. <see langword="null" /> is an ordinary resample pass; a known lens value
    ///     (e.g. <c>security</c>) runs a specialist prompt scoped to the files that lens targets.
    /// </summary>
    public string? Lens { get; set; }

    /// <summary>
    ///     Optional scope for this pass. <see langword="null" /> is the per-file default; a known scope value
    ///     (e.g. <c>pr_wide</c>) selects where the pass runs relative to the change set.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    ///     Whether this pass runs in shadow mode. Shadow passes are additive metadata for now; the review runtime does
    ///     not act on the flag yet.
    /// </summary>
    public bool Shadow { get; set; }

    /// <summary>
    ///     Reasoning effort this pass asks the model to spend. <see langword="null" /> is the default and means
    ///     <see cref="ReviewReasoningEffort.None" /> (no effort sent — current behavior).
    /// </summary>
    public ReviewReasoningEffort? ReasoningEffort { get; set; }

    public ClientRecord? Client { get; set; }
}
