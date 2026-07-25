// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

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
    string? Secret = null)
{
    /// <summary>Renders the request without its credential; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(AiConnectionWriteRequestDto)} {{ DisplayName = {this.DisplayName}, "
               + $"ProviderKind = {this.ProviderKind}, BaseUrl = {this.BaseUrl}, AuthMode = {this.AuthMode}, "
               + $"DiscoveryMode = {this.DiscoveryMode}, ConfiguredModels = {this.ConfiguredModels.Count}, "
               + $"PurposeBindings = {this.PurposeBindings.Count}, "
               + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}], "
               + $"Secret = {SecretSafeRendering.Elide(this.Secret)} }}";
    }
}
