// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One recorded change to the installation's license.
///     <para>
///         The rows are append-only and are not deleted with the license they describe, so removing a license
///         leaves the record of what the installation ran on intact.
///     </para>
/// </summary>
public sealed class LicenseActivationEventRecord
{
    /// <summary>Row identity. The rows carry no natural key: the same license may be activated more than once.</summary>
    public Guid Id { get; set; }

    /// <summary>What was done to the license.</summary>
    public LicenseActivationAction Action { get; set; }

    /// <summary>When it was done.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Who did it, when a signed-in user did.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    ///     The <c>jti</c> claim of the license concerned, or null when this build established no identity for
    ///     the document.
    /// </summary>
    public string? LicenseId { get; set; }

    /// <summary>The organization the license was issued to, on the same terms as <see cref="LicenseId" />.</summary>
    public string? Licensee { get; set; }
}
