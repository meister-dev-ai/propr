// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Provider-neutral request used by the AI connection repository to persist one profile.
/// </summary>
/// <remarks>The repository preserves creation correlation and rejects duplicate client/identifier pairs, including concurrent creation. Physical profile deletion permits reuse.</remarks>
/// <param name="CreationRequestId">Optional immutable client-scoped identifier used to recover creation through profile readback. An existing identifier rejects a duplicate creation.</param>
public sealed record AiConnectionWriteRequestDto(
    string DisplayName,
    string ProviderKind,
    string BaseUrl,
    string AuthMode,
    AiDiscoveryMode DiscoveryMode,
    IReadOnlyList<AiConfiguredModelDto> ConfiguredModels,
    IReadOnlyList<AiPurposeBindingDto> PurposeBindings,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    string? Secret = null,
    IReadOnlyDictionary<string, string>? ProviderSettings = null,
    IReadOnlyDictionary<string, string>? DeclaredSecrets = null,
    Guid? CreationRequestId = null)
{
    /// <summary>
    ///     Renders the request without its credential; see <see cref="SecretSafeRendering" />. The declared
    ///     configuration renders as its field names only, because a family decides what an operator puts there.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(AiConnectionWriteRequestDto)} {{ DisplayName = {this.DisplayName}, "
               + $"ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, AuthMode = {SecretSafeRendering.Identifier(this.AuthMode)}, "
               + $"DiscoveryMode = {this.DiscoveryMode}, ConfiguredModels = {this.ConfiguredModels.Count}, "
               + $"PurposeBindings = {this.PurposeBindings.Count}, "
               + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}], "
               + $"ProviderSettings = [{SecretSafeRendering.KeyNames(this.ProviderSettings)}], "
               + $"DeclaredSecrets = [{SecretSafeRendering.KeyNames(this.DeclaredSecrets)}], "
               + $"Secret = {SecretSafeRendering.Elide(this.Secret)} }}";
    }
}
