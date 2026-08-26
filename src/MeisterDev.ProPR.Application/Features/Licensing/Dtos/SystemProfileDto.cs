// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>
///     What the installation has observed about the system it runs on.
///     <para>
///         The profile is descriptive: nothing in verification, resolution, activation or quota enforcement
///         reads it.
///     </para>
/// </summary>
/// <param name="Current">The profile the installation currently reports, or null before the first observation.</param>
/// <param name="Drift">The recorded changes to the stable components, newest first.</param>
/// <param name="Hostnames">The host names the installation has been observed running on, most recently seen first.</param>
public sealed record SystemProfileDto(
    SystemProfileSnapshotDto? Current,
    IReadOnlyList<SystemProfileDriftDto> Drift,
    IReadOnlyList<ReplicaHostnameDto> Hostnames);

/// <summary>The installation's current profile.</summary>
/// <param name="ProfileHash">
///     SHA-256 over the canonical rendering of the stable components, in lower-case hexadecimal.
/// </param>
/// <param name="CapturedAt">When the profile was first recorded.</param>
/// <param name="UpdatedAt">When it was last observed, whether or not the observation changed anything.</param>
/// <param name="Stable">The components the hash is computed over.</param>
/// <param name="Volatile">The components describing the host the last observation came from.</param>
public sealed record SystemProfileSnapshotDto(
    string ProfileHash,
    DateTimeOffset CapturedAt,
    DateTimeOffset UpdatedAt,
    SystemProfileStableComponentsDto Stable,
    SystemProfileVolatileComponentsDto Volatile);

/// <summary>
///     The components that stay the same while the installation stays the same installation. A null member is a
///     component the installation could not observe, which is part of the profile rather than a gap in it.
/// </summary>
/// <param name="PostgresSystemIdentifier">
///     The PostgreSQL cluster's system identifier. Null when the database role may not execute the function
///     that reports it.
/// </param>
/// <param name="DatabaseName">The name of the database the installation keeps its state in.</param>
/// <param name="DatabaseOid">The object identifier PostgreSQL holds that database under.</param>
/// <param name="IdentityCreatedAt">When the installation's licensing identity was created, to whole seconds.</param>
/// <param name="ScmHostHashes">
///     The salted hashes of the configured SCM hosts, sorted. Null when no verified license is on file, because
///     the salt is derived from the license identifier. An empty list means a license is on file and no SCM
///     connection is configured. The plain host is not recorded anywhere.
/// </param>
public sealed record SystemProfileStableComponentsDto(
    string? PostgresSystemIdentifier,
    string? DatabaseName,
    long? DatabaseOid,
    DateTimeOffset? IdentityCreatedAt,
    IReadOnlyList<string>? ScmHostHashes);

/// <summary>
///     The components describing the host a replica runs on. None of them is part of the profile hash, and a
///     change to any of them records nothing.
/// </summary>
/// <param name="OperatingSystem">The operating system, as the runtime describes it.</param>
/// <param name="Runtime">The .NET runtime, as it describes itself.</param>
/// <param name="ProcessorCount">How many processors the replica sees.</param>
/// <param name="TotalAvailableMemoryBytes">How much memory is available to the replica, in bytes.</param>
/// <param name="TimeZoneId">The identifier of the replica's local time zone.</param>
/// <param name="MachineName">The host name of the replica that made the last observation.</param>
public sealed record SystemProfileVolatileComponentsDto(
    string? OperatingSystem,
    string? Runtime,
    int? ProcessorCount,
    long? TotalAvailableMemoryBytes,
    string? TimeZoneId,
    string? MachineName);

/// <summary>One recorded change to the installation's stable components.</summary>
/// <param name="OccurredAt">When the change was observed.</param>
/// <param name="ChangedComponents">Which components changed, named as the profile document names them.</param>
/// <param name="PreviousHash">The profile hash that held before the change.</param>
/// <param name="NewHash">The profile hash the installation reports after it.</param>
public sealed record SystemProfileDriftDto(
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> ChangedComponents,
    string PreviousHash,
    string NewHash);

/// <summary>A host name the installation has been observed running on.</summary>
/// <param name="Hostname">The host name the replica reported.</param>
/// <param name="FirstSeenAt">When it was first observed.</param>
/// <param name="LastSeenAt">When it was last observed.</param>
public sealed record ReplicaHostnameDto(string Hostname, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
