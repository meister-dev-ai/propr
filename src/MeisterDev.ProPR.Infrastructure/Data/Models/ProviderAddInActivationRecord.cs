// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One platform administrator's decision that the host may run one add-in binary.
/// </summary>
/// <remarks>
///     <para>
///         Keyed on the content hash, because the decision is about the bytes. Replacing the file in the add-in
///         directory changes its hash, and the new bytes have no row, so the host describes them and does not
///         run them until an administrator has looked at what the new version states.
///     </para>
///     <para>
///         What the add-in said about itself is copied onto the row rather than read back from the file. The row
///         is the record of what was approved, and a file that has since been replaced or removed would
///         otherwise leave an activation naming nothing.
///     </para>
/// </remarks>
public sealed class ProviderAddInActivationRecord
{
    public Guid Id { get; set; }

    /// <summary>The SHA-256 of the assembly, which the activation is bound to.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The identity key the add-in stated when it was activated.</summary>
    public string FamilyKey { get; set; } = string.Empty;

    /// <summary>What the add-in called itself when it was activated.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The version the add-in stated when it was activated.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The path the file sat at when it was activated.</summary>
    /// <remarks>
    ///     Recorded so an administrator can see where the decision was made about, and not read back: an
    ///     activated file that has been moved is the same bytes and stays activated.
    /// </remarks>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>The administrator who activated it, or null once that account has been removed.</summary>
    public Guid? ActivatedByAdminId { get; set; }

    /// <summary>That administrator's name as it stood at the time.</summary>
    public string ActivatedByDisplayName { get; set; } = string.Empty;

    /// <summary>When it was activated.</summary>
    public DateTimeOffset ActivatedAt { get; set; }
}
