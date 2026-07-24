// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     One logical model: a named role that maps to a concrete connection + configured model and carries that model's
///     execution settings (reasoning effort, protocol mode). The same shape describes both a tenant-catalog entry and a
///     per-client override — the scope (tenant id or client id) is supplied to the repository, not carried on the DTO.
/// </summary>
public sealed record LogicalModelDto(
    Guid Id,
    string Name,
    AiOperationKind Capability,
    Guid ConnectionId,
    Guid ConfiguredModelId,
    ReviewReasoningEffort ReasoningEffort,
    AiProtocolMode ProtocolMode)
{
    /// <summary>Returns <see langword="true" /> when this logical model is a chat model.</summary>
    public bool IsChat => this.Capability == AiOperationKind.Chat;

    /// <summary>Returns <see langword="true" /> when this logical model is an embedding model.</summary>
    public bool IsEmbedding => this.Capability == AiOperationKind.Embedding;
}
