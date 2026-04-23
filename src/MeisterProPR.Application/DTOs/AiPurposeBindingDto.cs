// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     One configured AI purpose binding for an AI connection profile.
/// </summary>
public sealed record AiPurposeBindingDto(
    Guid Id,
    AiPurpose Purpose,
    Guid? ConfiguredModelId = null,
    string? RemoteModelId = null,
    AiProtocolMode ProtocolMode = AiProtocolMode.Auto,
    bool IsEnabled = true,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null);
