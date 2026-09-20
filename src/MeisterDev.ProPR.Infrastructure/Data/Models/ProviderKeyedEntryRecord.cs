// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One short-lived named value set a provider add-in keeps between the start of a flow and its completion.
/// </summary>
/// <remarks>
///     Addressed by a key the add-in chose rather than by a connection, because the callback that reads it
///     carries an opaque state value and nothing else: the lookup is from that value to the connection, before
///     the connection is known.
/// </remarks>
public sealed class ProviderKeyedEntryRecord
{
    public Guid Id { get; set; }

    /// <summary>The add-in that owns the entry. Two add-ins using one entry key never see each other's.</summary>
    public string AddInKey { get; set; } = string.Empty;

    /// <summary>The key the add-in addresses the entry by, unique within that add-in.</summary>
    public string EntryKey { get; set; } = string.Empty;

    /// <summary>The named values, serialized and then protected by the host. An add-in never sees this form.</summary>
    public string ProtectedValue { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the entry stops being claimable. Mandatory, so nothing accumulates without an end.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the entry was consumed, set by the claim. Null while it is still claimable.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>
    ///     The administrator the host recorded when the entry was written, checked when it is claimed. Null for
    ///     an entry written outside an action invocation.
    /// </summary>
    public Guid? ActingPrincipalId { get; set; }
}
