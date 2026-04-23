// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Provider connection settings used for discovery, verification, or runtime creation.
/// </summary>
public sealed record AiConnectionProbeOptionsDto(
    AiProviderKind ProviderKind,
    string BaseUrl,
    AiAuthMode AuthMode,
    string? Secret = null,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null);
