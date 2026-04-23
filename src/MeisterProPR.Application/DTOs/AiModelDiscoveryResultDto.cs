// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Normalized model discovery payload returned by provider drivers.
/// </summary>
public sealed record AiModelDiscoveryResultDto(
    string DiscoveryStatus,
    bool ManualEntryAllowed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AiConfiguredModelDto> Models);
