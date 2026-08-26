// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     What an operator did to the installation's license. An installation holds one license, so replacing it
///     is recorded as a single action rather than as a removal followed by an activation: that keeps the record
///     of what the installation ran on continuous, with no instant in the history where it appears to have held
///     no license.
/// </summary>
public enum LicenseActivationAction
{
    /// <summary>A license was activated on an installation that had none on file.</summary>
    Activated = 1,

    /// <summary>A license was activated over one that was already on file.</summary>
    Replaced = 2,

    /// <summary>The license on file was removed.</summary>
    Removed = 3,
}
