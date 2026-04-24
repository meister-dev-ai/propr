// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Application.Features.Licensing.Dtos;

/// <summary>Installation-wide licensing summary for administration and session hydration.</summary>
public sealed record LicensingSummaryDto(
    InstallationEdition Edition,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<PremiumCapabilityDto> Capabilities);
