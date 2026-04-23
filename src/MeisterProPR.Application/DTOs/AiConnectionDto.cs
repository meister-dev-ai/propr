// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Data transfer object for a provider-neutral AI connection profile.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">Owning client ID.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="ProviderKind">Provider family.</param>
/// <param name="BaseUrl">Exact configured provider base URL.</param>
/// <param name="AuthMode">Authentication mode used for the provider.</param>
/// <param name="DiscoveryMode">Discovery mode for model configuration.</param>
/// <param name="IsActive">Whether this is the active profile for the client.</param>
/// <param name="ConfiguredModels">Configured models owned by this profile.</param>
/// <param name="PurposeBindings">Purpose bindings owned by this profile.</param>
/// <param name="Verification">Latest normalized verification snapshot.</param>
/// <param name="CreatedAt">When created.</param>
/// <param name="UpdatedAt">When last updated.</param>
/// <param name="DefaultHeaders">Optional default headers appended by the driver.</param>
/// <param name="DefaultQueryParams">Optional default query parameters appended by the driver.</param>
/// <param name="Secret">Protected secret material for internal use only; never serialized to JSON.</param>
[method: JsonConstructor]
public sealed record AiConnectionDto(
    Guid Id,
    Guid ClientId,
    string DisplayName,
    AiProviderKind ProviderKind,
    string BaseUrl,
    AiAuthMode AuthMode,
    AiDiscoveryMode DiscoveryMode,
    bool IsActive,
    IReadOnlyList<AiConfiguredModelDto> ConfiguredModels,
    IReadOnlyList<AiPurposeBindingDto> PurposeBindings,
    AiVerificationResultDto Verification,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    [property: JsonIgnore] string? Secret = null)
{
    /// <summary>Returns the effective model identifier bound to a specific purpose.</summary>
    public string? GetBoundModelId(AiPurpose purpose)
    {
        var binding = this.PurposeBindings.FirstOrDefault(candidate => candidate.Purpose == purpose && candidate.IsEnabled);

        if (binding is null)
        {
            return null;
        }

        return binding.RemoteModelId
               ?? this.ConfiguredModels.FirstOrDefault(model => model.Id == binding.ConfiguredModelId)?.RemoteModelId;
    }
}
