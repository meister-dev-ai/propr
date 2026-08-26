// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Commands.UpdateLicensing;

/// <summary>
///     Request to update the persisted per-capability overrides. The edition is not part of it: it follows from
///     the license the installation has activated rather than from a request.
/// </summary>
public sealed record UpdateLicensingCommand(
    IReadOnlyCollection<CapabilityOverrideMutation> CapabilityOverrides,
    Guid? ActorUserId);
