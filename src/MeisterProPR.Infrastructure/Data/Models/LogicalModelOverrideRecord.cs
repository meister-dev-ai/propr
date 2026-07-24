// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF persistence model for one per-client override of a logical model. Keyed by client + name, it shadows the
///     tenant-catalog <see cref="LogicalModelRecord" /> of the same name for that one client. On the system tenant this
///     is the only place logical models live (there is no tenant-catalog layer). An override may exist for a name that
///     has no tenant-catalog entry — matching is by name at resolution time, so the two records are not FK-linked.
/// </summary>
public sealed class LogicalModelOverrideRecord : ILogicalModelMapping
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public string Name { get; set; } = string.Empty;

    public AiOperationKind Capability { get; set; }

    public Guid ConnectionId { get; set; }

    public Guid ConfiguredModelId { get; set; }

    public ReviewReasoningEffort ReasoningEffort { get; set; }

    public AiProtocolMode ProtocolMode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
