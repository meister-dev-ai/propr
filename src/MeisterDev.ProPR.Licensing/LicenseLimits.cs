// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     The quantitative limits a license may state. Every member is optional and defaults to
///     <see cref="LicenseLimit.Absent" />, so a license that states none of them is the same as one that omits
///     the enclosing object.
/// </summary>
public sealed record LicenseLimits
{
    /// <summary>Limits that state nothing.</summary>
    public static LicenseLimits None { get; } = new();

    /// <summary>The distinct pull request authors allowed within one calendar month.</summary>
    public LicenseLimit AuthorsPerMonth { get; init; }

    /// <summary>The clients allowed on the installation.</summary>
    public LicenseLimit Clients { get; init; }

    /// <summary>The runners allowed to enroll.</summary>
    public LicenseLimit Runners { get; init; }

    /// <summary>The reviews allowed to execute at the same time.</summary>
    public LicenseLimit ConcurrentReviews { get; init; }

    /// <summary>Whether every member is absent.</summary>
    public bool IsEmpty =>
        this.AuthorsPerMonth.IsAbsent
        && this.Clients.IsAbsent
        && this.Runners.IsAbsent
        && this.ConcurrentReviews.IsAbsent;
}
