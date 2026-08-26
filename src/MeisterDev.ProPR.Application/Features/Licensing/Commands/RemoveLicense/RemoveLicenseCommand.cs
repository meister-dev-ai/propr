// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Commands.RemoveLicense;

/// <summary>Request to remove the license the installation has on file.</summary>
/// <param name="ActorUserId">Who is removing it, when a signed-in user is.</param>
public sealed record RemoveLicenseCommand(Guid? ActorUserId);
