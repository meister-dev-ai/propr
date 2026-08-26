// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     One recorded change to the installation's license.
///     <para>
///         The record outlives the license it describes, so an installation that has removed its license can
///         still answer which license it ran on and who changed it. It carries no part of the license document
///         itself, only the identity the document claimed.
///     </para>
/// </summary>
public sealed record LicenseActivationEvent
{
    /// <summary>What was done to the license.</summary>
    public required LicenseActivationAction Action { get; init; }

    /// <summary>When it was done, in UTC.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    ///     Who did it, when a signed-in user did. Null covers a change made by something other than a signed-in
    ///     operator; the record is written either way.
    /// </summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>
    ///     The <c>jti</c> claim of the license the action concerns, or null when the identity could not be
    ///     established. A removal of a document that no longer verifies has no identity this build accepts, and
    ///     recording what such a document claims would put unverified text into the record.
    /// </summary>
    public string? LicenseId { get; init; }

    /// <summary>The organization the license was issued to, on the same terms as <see cref="LicenseId" />.</summary>
    public string? Licensee { get; init; }
}
