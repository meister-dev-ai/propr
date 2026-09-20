// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

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
    string ProtocolMode)
{
    /// <summary>
    ///     The stored protocol mode when no loaded family claims the protocol mode it holds, or
    ///     <see langword="null" /> when <see cref="ProtocolMode" /> resolved.
    /// </summary>
    /// <remarks>
    ///     A protocol mode travels with the family that speaks it, so a row can hold a name nothing claims: a
    ///     family that has been removed, or one whose modes were declared by a later build.
    ///     <see cref="ProtocolMode" /> stands in as the host-reserved automatic shape for such a row, and this
    ///     property distinguishes it from a row that selected that shape. The read does not throw, so one
    ///     unreadable row does not take out the list it is read in.
    /// </remarks>
    public string? UnresolvedProtocolMode { get; init; }

    /// <summary>Returns <see langword="true" /> when this logical model is a chat model.</summary>
    public bool IsChat => this.Capability == AiOperationKind.Chat;

    /// <summary>Returns <see langword="true" /> when this logical model is an embedding model.</summary>
    public bool IsEmbedding => this.Capability == AiOperationKind.Embedding;
}
