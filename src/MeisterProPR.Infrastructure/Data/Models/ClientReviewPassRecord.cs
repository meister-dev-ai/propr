// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
    public Guid ConfiguredModelId { get; set; }

    /// <summary>
    ///     Optional lens for this pass. <see langword="null" /> is an ordinary resample pass; a known lens value
    ///     (e.g. <c>security</c>) runs a specialist prompt scoped to the files that lens targets.
    /// </summary>
    public string? Lens { get; set; }

    public ClientRecord? Client { get; set; }
}
