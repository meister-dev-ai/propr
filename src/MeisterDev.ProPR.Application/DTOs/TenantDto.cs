// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Tenant boundary data returned by administration and tenant-auth flows.</summary>
/// <param name="Id">Tenant identifier.</param>
/// <param name="Slug">URL-safe tenant key.</param>
/// <param name="DisplayName">Human-readable tenant name.</param>
/// <param name="IsActive">Whether the tenant is active.</param>
/// <param name="LocalLoginEnabled">Whether local (non-SSO) login is permitted.</param>
/// <param name="IsEditable">Whether the tenant's policy may be edited at all.</param>
/// <param name="CreatedAt">When the tenant was created.</param>
/// <param name="UpdatedAt">When the tenant was last updated.</param>
/// <param name="AllowedAiProviderKinds">
///     Provider families this tenant's clients may use, by identity key, out of the entries a loaded family
///     claims. Empty together with an empty <paramref name="UnresolvedAiProviderKinds" /> means unrestricted; a
///     tenant that has never stated a policy reads that way.
/// </param>
/// <param name="AllowedAiEndpointHosts">
///     Endpoint hosts this tenant's clients may send AI traffic to. Empty means unrestricted. An entry matches a
///     host exactly, or any subdomain of it when written with a leading dot.
/// </param>
/// <param name="UnresolvedAiProviderKinds">
///     Permitted-family entries no loaded family claims, as they are stored. They permit no family, so a tenant
///     with entries here and none in <paramref name="AllowedAiProviderKinds" /> refuses every provider. Reported
///     so an operator can see which entry has to be corrected, and named on a write to remove one.
/// </param>
public sealed record TenantDto(
    Guid Id,
    string Slug,
    string DisplayName,
    bool IsActive,
    bool LocalLoginEnabled,
    bool IsEditable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? AllowedAiProviderKinds = null,
    IReadOnlyList<string>? AllowedAiEndpointHosts = null,
    IReadOnlyList<string>? UnresolvedAiProviderKinds = null);
