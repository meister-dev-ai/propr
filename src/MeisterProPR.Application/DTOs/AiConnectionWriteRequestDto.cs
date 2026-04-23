// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Provider-neutral request used by the AI connection repository to persist one profile.
/// </summary>
public sealed record AiConnectionWriteRequestDto(
    string DisplayName,
    AiProviderKind ProviderKind,
    string BaseUrl,
    AiAuthMode AuthMode,
    AiDiscoveryMode DiscoveryMode,
    IReadOnlyList<AiConfiguredModelDto> ConfiguredModels,
    IReadOnlyList<AiPurposeBindingDto> PurposeBindings,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    string? Secret = null);
