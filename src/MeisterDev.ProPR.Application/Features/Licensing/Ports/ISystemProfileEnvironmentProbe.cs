// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Reads what the replica's own host looks like. Nothing it returns is part of the profile hash.
/// </summary>
public interface ISystemProfileEnvironmentProbe
{
    /// <summary>Reads the host's description as the running process sees it.</summary>
    /// <returns>The observed volatile components.</returns>
    SystemProfileVolatileComponents Read();
}
