// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     The highest instant this installation has observed.
///     <para>
///         Licensing reads the row to judge license terms against a value that cannot be lowered by moving the
///         host clock back. The row is created the first time an instant is recorded, so an installation that has
///         not yet evaluated a license term has no row.
///     </para>
/// </summary>
public sealed class InstallationObservedTimeRecord
{
    /// <summary>Fixed key. An installation observes one timeline.</summary>
    public int Id { get; set; }

    /// <summary>The latest instant any replica of this installation has reported.</summary>
    public DateTimeOffset ObservedAt { get; set; }
}
