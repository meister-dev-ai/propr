// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>One entry of the installation's license activation history.</summary>
/// <param name="Action">What was done to the license.</param>
/// <param name="OccurredAt">When it was done, in UTC.</param>
/// <param name="ActorUserId">Who did it, when a signed-in user did.</param>
/// <param name="LicenseId">The <c>jti</c> claim of the license concerned, when it could be established.</param>
/// <param name="Licensee">The organization the license was issued to, when it could be established.</param>
public sealed record LicenseActivationEventDto(
    LicenseActivationAction Action,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? LicenseId,
    string? Licensee);
