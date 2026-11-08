// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     One configured AI purpose binding for an AI connection profile.
/// </summary>
/// <param name="ReasoningLevel">
///     The reasoning level the purpose runs at on the bound model, or null when the binding sets none. The workspace
///     selection sets it; a connection save keeps the stored level while the binding keeps its model and ignores this
///     value.
/// </param>
public sealed record AiPurposeBindingDto(
    Guid Id,
    AiPurpose Purpose,
    Guid? ConfiguredModelId = null,
    string? RemoteModelId = null,
    string ProtocolMode = ProviderDeclaredProtocolModes.Auto,
    bool IsEnabled = true,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? ReasoningLevel = null);
