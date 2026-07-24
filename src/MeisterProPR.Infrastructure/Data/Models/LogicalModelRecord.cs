// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF persistence model for one tenant-catalog logical model: a named role, scoped to a tenant, that maps to a
///     concrete connection + configured model and carries that model's execution settings. A per-client
///     <see cref="LogicalModelOverrideRecord" /> of the same name shadows this entry for that client. The system
///     tenant has no rows here — it stores per-client overrides only.
/// </summary>
public sealed class LogicalModelRecord : ILogicalModelMapping
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public AiOperationKind Capability { get; set; }

    public Guid ConnectionId { get; set; }

    public Guid ConfiguredModelId { get; set; }

    public ReviewReasoningEffort ReasoningEffort { get; set; }

    public AiProtocolMode ProtocolMode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
