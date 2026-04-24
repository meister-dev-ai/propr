// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Application.Features.Licensing.Commands.UpdateLicensing;

/// <summary>Request to update the installation edition and persisted capability overrides.</summary>
public sealed record UpdateLicensingCommand(
    InstallationEdition Edition,
    IReadOnlyCollection<CapabilityOverrideMutation> CapabilityOverrides,
    Guid? ActorUserId);
