// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Data transfer object for a provider-neutral AI connection profile.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">Owning client ID for a client-scoped connection; null for a tenant-scoped one.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="ProviderKind">
///     Provider family, by identity key. A key no loaded family claims is carried as it is stored, so a profile
///     whose add-in is absent is still listed and can be corrected.
/// </param>
/// <param name="BaseUrl">Exact configured provider base URL.</param>
/// <param name="AuthMode">
///     Authentication mode used for the provider, by the name it persists under. A name no loaded family claims
///     is carried as it is stored, for the same reason <paramref name="ProviderKind" /> is.
/// </param>
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
/// <param name="TenantId">Owning tenant ID for a tenant-scoped connection (inherited by the tenant's clients); null for a client-scoped one.</param>
[method: JsonConstructor]
public sealed record AiConnectionDto(
    Guid Id,
    Guid? ClientId,
    string DisplayName,
    string ProviderKind,
    string BaseUrl,
    string AuthMode,
    AiDiscoveryMode DiscoveryMode,
    bool IsActive,
    IReadOnlyList<AiConfiguredModelDto> ConfiguredModels,
    IReadOnlyList<AiPurposeBindingDto> PurposeBindings,
    AiVerificationResultDto Verification,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null,
    [property: JsonIgnore] string? Secret = null,
    Guid? TenantId = null)
{
    /// <summary>
    ///     Whether this profile can be used as it is stored, and what stands in the way when it cannot.
    /// </summary>
    /// <remarks>
    ///     Declared as an initialized property rather than a constructor parameter so a caller that builds a
    ///     profile from values it already holds, rather than from stored text, reports it usable without saying
    ///     so.
    /// </remarks>
    public AiConnectionAvailabilityDto Availability { get; init; } = AiConnectionAvailabilityDto.Available;

    /// <summary>
    ///     The non-secret configuration values this connection's provider family declared, by declared field name,
    ///     or null for a family that declares none.
    /// </summary>
    /// <remarks>
    ///     Only the names the family declares today are reported. A value left behind by a field the family has
    ///     since dropped stays stored and is not returned, so rolling that family version back gets it back.
    ///     Declared as an initialized property rather than a constructor parameter for the same reason
    ///     <see cref="Availability" /> is: a caller building a profile from values it already holds says nothing
    ///     about declared configuration.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ProviderSettings { get; init; }

    /// <summary>
    ///     The values of the family's secret-marked declared fields, by declared field name. For internal use
    ///     only; never serialized to JSON, for the same reason <see cref="Secret" /> is not.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> DeclaredSecrets { get; init; } = new Dictionary<string, string>();

    /// <summary>
    ///     Which secret-marked declared fields hold a stored value. A console renders a field as set or not set
    ///     from this and offers to replace it, which the credential fields already do.
    /// </summary>
    public IReadOnlyList<string> DeclaredSecretNames => [.. this.DeclaredSecrets.Keys];

    /// <summary>
    ///     Merges the three sources into the one set a family reads back: its configuration settings, its
    ///     secret-marked settings and the fields of the credential stored for an authentication mode.
    /// </summary>
    /// <remarks>
    ///     The credential is included because a mode whose credential is several values — an access key id, a
    ///     secret access key and an optional session token — is stored as one protected column holding an
    ///     envelope, and the envelope is the host's storage format: a family loaded from a directory compiles
    ///     against the contract alone and cannot name the type that reads it. Reading it here is what lets such a
    ///     family receive its credential under the field names it declared, the same way it receives every other
    ///     declared value. A mode whose credential is one string yields that one field, which the family
    ///     already receives on <see cref="Secret" />.
    /// </remarks>
    /// <param name="settings">The family's non-secret declared settings, or null where it declares none.</param>
    /// <param name="declaredSecrets">The values of its secret-marked declared settings.</param>
    /// <param name="storedSecret">The stored credential, as one protected value.</param>
    /// <param name="authMode">The authentication mode the credential was stored for.</param>
    internal static IReadOnlyDictionary<string, string> MergeDeclaredValues(
        IReadOnlyDictionary<string, string>? settings,
        IReadOnlyDictionary<string, string> declaredSecrets,
        string? storedSecret,
        string authMode)
    {
        var merged = settings is { Count: > 0 }
            ? new Dictionary<string, string>(settings, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in declaredSecrets)
        {
            merged[name] = value;
        }

        // Last, so that where a family named the same field on both axes the credential value is the one it
        // reads back. The two are separate namespaces and a collision is the family's own mistake; resolving it
        // toward the credential keeps a call from being signed with a configuration value.
        foreach (var (name, value) in ProviderSecretEnvelope.Decode(storedSecret, authMode).Fields)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                merged[name] = value;
            }
        }

        return merged;
    }

    /// <summary>
    ///     The configuration fields this connection's provider family declares, so a console can render the form
    ///     for a family it has never seen. Empty for a family that declares none.
    /// </summary>
    public IReadOnlyList<AiDeclaredFieldDto> DeclaredFields { get; init; } = [];

    /// <summary>
    ///     The current value of each read-only computed field, recomputed for this read. Empty for a family that
    ///     declares none.
    /// </summary>
    public IReadOnlyList<AiComputedFieldDto> ComputedFields { get; init; } = [];

    /// <summary>
    ///     The operations this connection's provider family declares, so a console can offer them without
    ///     knowing which family it is looking at. Empty for a family that declares none.
    /// </summary>
    /// <remarks>
    ///     Carried on the connection rather than on the family, because what an operator has to be told before
    ///     starting one depends on how this connection is configured.
    /// </remarks>
    public IReadOnlyList<AiDeclaredActionDto> DeclaredActions { get; init; } = [];

    /// <summary>
    ///     Which of the declared actions this connection is offered, by action id, as the family decided from the
    ///     connection's own state.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="DeclaredActions" /> because the two answer different questions: the
    ///     declaration says which operations the family has, and this says which of them apply here. A family
    ///     whose credential is written by its own sign-in offers the sign-in before that has happened and the
    ///     disconnect after, from one declaration. A console renders the declared actions whose id is in this
    ///     list.
    /// </remarks>
    public IReadOnlyList<string> OfferedActionIds { get; init; } = [];

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

    /// <summary>
    ///     Renders the profile without its credential. <c>[JsonIgnore]</c> keeps the secret out of API responses,
    ///     but the generated <c>ToString</c> would still print it into any log line or exception message that
    ///     mentions the profile, which no serialization attribute can prevent.
    ///     <para>
    ///         The declared configuration is rendered as its field names only. A family chooses what goes in it,
    ///         so a value there is whatever that family asked an operator for, and the host has no basis for
    ///         deciding which of those is safe to print.
    ///     </para>
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(AiConnectionDto)} {{ Id = {this.Id}, DisplayName = {this.DisplayName}, "
               + $"ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, AuthMode = {SecretSafeRendering.Identifier(this.AuthMode)}, "
               + $"ClientId = {this.ClientId}, TenantId = {this.TenantId}, IsActive = {this.IsActive}, "
               + $"ConfiguredModels = {this.ConfiguredModels.Count}, PurposeBindings = {this.PurposeBindings.Count}, "
               + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
               + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}], "
               + $"ProviderSettings = [{SecretSafeRendering.KeyNames(this.ProviderSettings)}], "
               + $"DeclaredSecrets = [{SecretSafeRendering.KeyNames(this.DeclaredSecrets)}], "
               + $"Secret = {SecretSafeRendering.Elide(this.Secret)} }}";
    }
}
