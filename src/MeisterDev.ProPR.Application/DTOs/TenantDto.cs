// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

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
/// <param name="InstallationDefaultCapturesReasoning">
///     The current value of the installation-wide reasoning-capture switch, so a console can name what the
///     installation default does for this tenant. Stated on every construction and carrying no default of its
///     own: a caller that omitted it would report a switch value it never read.
/// </param>
/// <param name="ReasoningCapturePolicy">
///     Whether this tenant's review jobs capture model reasoning into the protocol.
///     <see cref="Domain.Enums.ReasoningCapturePolicy.InstallationDefault" /> leaves the decision to the
///     installation switch reported in <paramref name="InstallationDefaultCapturesReasoning" />.
/// </param>
/// <param name="Budget">The tenant's monthly USD budget caps. Null caps mean no tenant-level limit.</param>
/// <param name="ReviewLimits">The tenant's per-file byte limits. Null values leave the installation values in force.</param>
public sealed record TenantDto(
    Guid Id,
    string Slug,
    string DisplayName,
    bool IsActive,
    bool LocalLoginEnabled,
    bool IsEditable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool InstallationDefaultCapturesReasoning,
    IReadOnlyList<string>? AllowedAiProviderKinds = null,
    IReadOnlyList<string>? AllowedAiEndpointHosts = null,
    IReadOnlyList<string>? UnresolvedAiProviderKinds = null,
    ReasoningCapturePolicy ReasoningCapturePolicy = ReasoningCapturePolicy.InstallationDefault,
    TenantBudgetConfigDto? Budget = null,
    TenantReviewLimitsDto? ReviewLimits = null);

/// <summary>
///     A tenant's per-file byte limits for reviews. Both values are optional; a null value leaves the
///     installation value in force. They bound what one file may contribute to a review, so they protect the
///     host that runs it.
/// </summary>
public sealed record TenantReviewLimitsDto(
    int? MaxFileSizeBytes = null,
    int? MaxStructuralParseBytes = null);

/// <summary>
///     A tenant's monthly USD budget caps, covering the month-to-date spend of every client in the tenant. Both
///     values are optional; a null cap means no limit. The soft cap stops admitting new review jobs, the hard cap
///     cuts further model calls. The tenant scope is evaluated after the client scopes, so a tighter client cap
///     binds first.
/// </summary>
public sealed record TenantBudgetConfigDto(
    decimal? MonthlySoftCapUsd = null,
    decimal? MonthlyHardCapUsd = null);
