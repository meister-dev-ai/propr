// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;

/// <summary>Request to activate a license document, replacing whatever the installation had on file.</summary>
/// <param name="CompactLicense">
///     The license document as the operator supplied it. It is unverified input at this point: pasted text and
///     the contents of a file the browser read are the same value here.
/// </param>
/// <param name="ActorUserId">Who is activating it, when a signed-in user is.</param>
public sealed record ActivateLicenseCommand(string CompactLicense, Guid? ActorUserId);
