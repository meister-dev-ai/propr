// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>
///     API-facing representation of one quantitative limit: what the license states for it, what the
///     installation is held to, and what it currently holds.
///     <para>
///         The stated allowance and the effective ceiling are separate fields because they disagree in cases
///         an operator has to be able to read: a limit the license leaves out is enforced at the community
///         value, and a license past its grace window states a number that no longer applies. The effective
///         ceiling is the one a refusal quotes.
///     </para>
/// </summary>
/// <param name="Key">The dimension the limit constrains.</param>
/// <param name="Allowance">What the license states for it.</param>
/// <param name="LicensedCount">The stated ceiling. Set exactly when the allowance is a count.</param>
/// <param name="InformationalCount">
///     What the installation currently holds for this dimension. It is reported for information: no license
///     check, activation or capability resolution reads it, and it is not the value an enforcement decision
///     would use. Null when the dimension is not measured, and null on a read that does not gather counts.
/// </param>
/// <param name="EffectiveCeiling">
///     What kind of ceiling the installation is held to, as an enforcement decision reads it. Null on the
///     responses that report the summary without resolving ceilings, which are the two administration
///     mutations: activating a license, and patching the capability overrides.
/// </param>
/// <param name="EffectiveCount">
///     The ceiling the installation is held to. Set exactly when the effective ceiling is a count.
/// </param>
/// <param name="EffectiveSource">
///     Which side of the licensing rules produced the effective ceiling. Null on the same two mutation
///     responses as the effective ceiling.
/// </param>
/// <param name="ExcludedAutomationCount">
///     How many automation identities the exclusion rules kept out of this dimension's current number. Set for
///     authors per month, where automated pull requests and questions are left out of the count. Null for a
///     dimension no exclusion applies to, and null on a read that does not gather counts.
/// </param>
public sealed record LicenseLimitDto(
    LicenseLimitKey Key,
    LicenseLimitAllowance Allowance,
    long? LicensedCount = null,
    long? InformationalCount = null,
    LicenseLimitCeiling? EffectiveCeiling = null,
    long? EffectiveCount = null,
    LicenseLimitSource? EffectiveSource = null,
    long? ExcludedAutomationCount = null);
