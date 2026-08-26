// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     The observed components that stay the same while an installation stays the same installation, and which
///     the profile hash is computed over.
///     <para>
///         Every component is optional. A component the installation cannot observe is recorded as absent rather
///         than omitted, and absence is hashed as such, so two installations that differ only in what their
///         database role was allowed to read do not share a profile hash.
///     </para>
///     <para>
///         The property names carried here are the names used in the canonical rendering, in the persisted
///         document and in a drift record's changed-component list, so one name identifies a component
///         everywhere it appears.
///     </para>
/// </summary>
public sealed record SystemProfileStableComponents
{
    /// <summary>
    ///     The PostgreSQL cluster's system identifier, as a decimal string.
    ///     <para>
    ///         Absent when the database role may not execute the function that reports it. An installation can
    ///         take that privilege away and a managed PostgreSQL service may withhold it, so absence is an
    ///         expected reading rather than a fault.
    ///     </para>
    ///     <para>
    ///         PostgreSQL exposes the identifier as a signed 64-bit integer, so a cluster whose identifier is
    ///         above 2^63 renders here as a negative decimal string. The value is stable either way, but it
    ///         will not match the unsigned form <c>pg_controldata</c> prints for such a cluster.
    ///     </para>
    /// </summary>
    [JsonPropertyName("postgresSystemIdentifier")]
    public string? PostgresSystemIdentifier { get; init; }

    /// <summary>The name of the database this installation keeps its state in.</summary>
    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; init; }

    /// <summary>
    ///     The object identifier PostgreSQL holds that database under. It distinguishes a database that was
    ///     dropped and recreated under the same name from the original.
    /// </summary>
    [JsonPropertyName("databaseOid")]
    public long? DatabaseOid { get; init; }

    /// <summary>
    ///     When the installation's licensing identity was created, in whole seconds since the Unix epoch. It is
    ///     the instant the installation was first seen.
    /// </summary>
    [JsonPropertyName("identityCreatedAtUnixSeconds")]
    public long? IdentityCreatedAtUnixSeconds { get; init; }

    /// <summary>
    ///     The salted hashes of the SCM hosts this installation is configured against, as a sorted set.
    ///     <para>
    ///         Absent when no verified license is on file, because the salt is derived from the license
    ///         identifier. The plain host is never carried here or persisted anywhere in the profile.
    ///     </para>
    /// </summary>
    [JsonPropertyName("scmHostHashes")]
    public IReadOnlyList<string>? ScmHostHashes { get; init; }
}
