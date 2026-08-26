// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>
///     Installation-wide licensing summary for administration and session hydration.
/// </summary>
/// <param name="Edition">The edition the installation currently runs as.</param>
/// <param name="ActivatedAt">When the license in force was activated, when one is in force.</param>
/// <param name="Capabilities">The effective state of every premium capability.</param>
/// <param name="Stage">Where the installation stands in its license's lifecycle.</param>
/// <param name="NotBefore">When the license term begins.</param>
/// <param name="WarningStartsAt">
///     When the installation starts being reported as approaching expiry. Never earlier than the start of the
///     term, so a term shorter than the warning window reports its own start.
/// </param>
/// <param name="ExpiresAt">When the license term ends.</param>
/// <param name="GraceEndsAt">When the grace window after the term ends, after which the installation is Community.</param>
/// <param name="DaysRemaining">
///     Whole days of entitlement left: until the term ends while it is running, until the grace window ends once
///     the term has, and zero once both have. Null before the term begins, when no entitlement has started, and
///     null when there is no license to read a term from.
/// </param>
/// <param name="LicensingIdentity">
///     The identifier this installation reports itself under, for telling usage reports from different
///     installations apart and for quoting in a support conversation. It binds nothing: no license is issued
///     against it and no license check reads it. Null on a host that has no licensing store to read it from.
/// </param>
/// <param name="Licensee">
///     The organization the license in force was issued to. Null when no license is verified.
/// </param>
/// <param name="LicenseId">The identifier of the license in force, for quoting in a renewal or support request.</param>
/// <param name="Limits">
///     Every quantitative limit, each with what the license states for it, the ceiling the installation is
///     held to, and what it currently holds. The effective ceiling and the count are filled in by the
///     administration read alone; the responses to activating a license and to patching the capability
///     overrides carry the stated allowance only. Null on a host that reports no license state at all.
/// </param>
/// <param name="AuthorOverage">
///     Where the current calendar month's counted authors stand against the number the license states for them.
///     Filled in by the administration read alone. Null when there is no number to compare against, which is no
///     license in force, a license that leaves the author limit out, and one that states it as unlimited, and
///     null on a host with no rollup to count.
/// </param>
/// <param name="AuthorPeakMonth">
///     The busiest of the twelve calendar months ending with the current one. Filled in by the administration
///     read alone. Null when the window holds no counted author, and null on a host with no rollup to read.
/// </param>
public sealed record LicensingSummaryDto(
    InstallationEdition Edition,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<PremiumCapabilityDto> Capabilities,
    LicenseStage Stage = LicenseStage.None,
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? WarningStartsAt = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? GraceEndsAt = null,
    int? DaysRemaining = null,
    Guid? LicensingIdentity = null,
    string? Licensee = null,
    string? LicenseId = null,
    IReadOnlyList<LicenseLimitDto>? Limits = null,
    AuthorOverageDto? AuthorOverage = null,
    AuthorPeakMonthDto? AuthorPeakMonth = null);
