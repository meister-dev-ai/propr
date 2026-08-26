// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One recorded change to the installation's stable components. Append-only: the records survive the
///     profile they describe.
/// </summary>
public sealed class LicensingSystemProfileDriftRecord
{
    /// <summary>Row key.</summary>
    public Guid Id { get; set; }

    /// <summary>When the change was observed.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Which components changed, as a JSON array of the names the profile document uses.</summary>
    public string ChangedComponents { get; set; } = string.Empty;

    /// <summary>The profile hash that held before the change.</summary>
    public string PreviousHash { get; set; } = string.Empty;

    /// <summary>The profile hash the installation reports after it.</summary>
    public string NewHash { get; set; } = string.Empty;
}
