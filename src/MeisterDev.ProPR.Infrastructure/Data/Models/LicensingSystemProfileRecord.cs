// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     The profile the installation currently reports about the system it runs on.
///     <para>
///         One row per installation. Deleting it discards the baseline: the next observation captures a new
///         one, with no record of what the installation looked like before.
///     </para>
/// </summary>
public sealed class LicensingSystemProfileRecord
{
    /// <summary>Fixed key. The profile is installation-wide.</summary>
    public int Id { get; set; }

    /// <summary>
    ///     The stable components as JSON, under the member names the profile document uses. The hash is stored
    ///     beside them rather than derived from this column, because a jsonb value is stored in the database's
    ///     own member order and not in the one the hash was computed over.
    /// </summary>
    public string StableComponents { get; set; } = string.Empty;

    /// <summary>The volatile components of the host the last observation came from.</summary>
    public string VolatileComponents { get; set; } = string.Empty;

    /// <summary>SHA-256 over the canonical stable document, in lower-case hexadecimal.</summary>
    public string ProfileHash { get; set; } = string.Empty;

    /// <summary>When the profile was first recorded. It does not move once set.</summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>When the profile was last observed, whether or not the observation changed anything.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
