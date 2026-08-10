// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>Persisted singleton row representing the active installation edition.</summary>
public sealed class InstallationEditionRecord
{
    public int Id { get; set; }

    public InstallationEdition Edition { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public Guid? ActivatedByUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    ///     How many runners may hold leases at once, or null when leasing is not metered.
    ///     <para>
    ///         Stored rather than compiled, so a large installation or a hosted offering raises it without a
    ///         new build. Null is deliberately "unmetered" rather than "zero": until the signed entitlement
    ///         lands there is no authority to read a number from, and defaulting an unset installation to
    ///         zero would refuse every lease on an install that never opted into metering at all.
    ///     </para>
    /// </summary>
    public int? EntitledRunnerSlots { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
