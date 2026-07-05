// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF persistence model for one entry in a client's ordered review-pass list. Each entry binds one additional
///     multi-pass union pass (after the implicit tier baseline) to a configured model, at the given ordinal.
/// </summary>
public sealed class ClientReviewPassRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public int Ordinal { get; set; }
    public Guid ConfiguredModelId { get; set; }

    public ClientRecord? Client { get; set; }
}
