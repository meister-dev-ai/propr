// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF persistence model for a per-client mapping from an internal AI purpose to a named logical model. When a
///     purpose is mapped here, it resolves through the logical-model catalog (connection, model, protocol from the
///     resolved role); when a purpose has no row, resolution falls back to the client's active AI purpose bindings.
/// </summary>
public sealed class ClientPurposeLogicalModelRecord
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public AiPurpose Purpose { get; set; }

    public string LogicalModelName { get; set; } = string.Empty;

    public ClientRecord? Client { get; set; }
}
