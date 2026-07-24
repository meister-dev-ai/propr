// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     The columns shared by a logical-model tenant-catalog entry and a per-client override: the mapping to a concrete
///     connection + configured model plus the execution settings carried on the mapping. Scope keys (tenant id vs
///     client id) are the only difference between the two records and are therefore not part of this contract.
/// </summary>
public interface ILogicalModelMapping
{
    /// <summary>Surrogate identity of the row.</summary>
    Guid Id { get; }

    /// <summary>The role name selected by a pass or purpose. Unique within its scope.</summary>
    string Name { get; }

    /// <summary>Whether this logical model is a chat or an embedding model.</summary>
    AiOperationKind Capability { get; }

    /// <summary>The connection profile the role maps to.</summary>
    Guid ConnectionId { get; }

    /// <summary>The configured model under that connection the role maps to.</summary>
    Guid ConfiguredModelId { get; }

    /// <summary>Reasoning effort carried on the mapping (single source of truth).</summary>
    ReviewReasoningEffort ReasoningEffort { get; }

    /// <summary>Protocol mode carried on the mapping (single source of truth).</summary>
    AiProtocolMode ProtocolMode { get; }
}
