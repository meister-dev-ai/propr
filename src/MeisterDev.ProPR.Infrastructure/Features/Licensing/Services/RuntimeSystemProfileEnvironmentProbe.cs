// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Runtime.InteropServices;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;

/// <summary>
///     Describes the replica's host from what the running process can see about itself.
///     <para>
///         Everything here is read from the process and its runtime. Nothing identifying the hardware is read:
///         no machine id, no network adapter address and no processor serial.
///     </para>
/// </summary>
public sealed class RuntimeSystemProfileEnvironmentProbe : ISystemProfileEnvironmentProbe
{
    /// <inheritdoc />
    public SystemProfileVolatileComponents Read()
    {
        return new SystemProfileVolatileComponents
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            TimeZoneId = TimeZoneInfo.Local.Id,
            MachineName = Environment.MachineName,
        };
    }
}
