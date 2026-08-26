// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     The observed components that describe the host a replica is running on right now.
///     <para>
///         None of them is part of the profile hash. They change when an installation is moved, resized or
///         redeployed, all of which leave it the same installation, so a change here updates the recorded
///         profile without being reported as drift.
///     </para>
/// </summary>
public sealed record SystemProfileVolatileComponents
{
    /// <summary>The operating system the replica is running on, as the runtime describes it.</summary>
    [JsonPropertyName("operatingSystem")]
    public string? OperatingSystem { get; init; }

    /// <summary>The .NET runtime the replica is running on, as the runtime describes it.</summary>
    [JsonPropertyName("runtime")]
    public string? Runtime { get; init; }

    /// <summary>How many processors the replica sees.</summary>
    [JsonPropertyName("processorCount")]
    public int? ProcessorCount { get; init; }

    /// <summary>How much memory is available to the replica, in bytes.</summary>
    [JsonPropertyName("totalAvailableMemoryBytes")]
    public long? TotalAvailableMemoryBytes { get; init; }

    /// <summary>The identifier of the replica's local time zone.</summary>
    [JsonPropertyName("timeZoneId")]
    public string? TimeZoneId { get; init; }

    /// <summary>
    ///     The host name of the replica that recorded this observation. Every host name observed is also
    ///     accumulated in the replica host name set, so this one names the most recent observer rather than the
    ///     installation.
    /// </summary>
    [JsonPropertyName("machineName")]
    public string? MachineName { get; init; }
}
