// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

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
///     Provider families this tenant's clients may use. Empty means unrestricted, which is what a tenant that has
///     never stated a policy looks like.
/// </param>
/// <param name="AllowedAiEndpointHosts">
///     Endpoint hosts this tenant's clients may send AI traffic to. Empty means unrestricted. An entry matches a
///     host exactly, or any subdomain of it when written with a leading dot.
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
    IReadOnlyList<AiProviderKind>? AllowedAiProviderKinds = null,
    IReadOnlyList<string>? AllowedAiEndpointHosts = null);
