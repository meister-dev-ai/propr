// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Provider connection settings used for discovery, verification, or runtime creation.
/// </summary>
public sealed record AiConnectionProbeOptionsDto(
    string ProviderKind,
    string BaseUrl,
    string AuthMode,
    string? Secret = null,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null)
{
    /// <summary>Renders the probe target without its credential; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return
            $"{nameof(AiConnectionProbeOptionsDto)} {{ ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, "
            + $"AuthMode = {SecretSafeRendering.Identifier(this.AuthMode)}, Secret = {SecretSafeRendering.Elide(this.Secret)}, "
            + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
            + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}] }}";
    }
}
