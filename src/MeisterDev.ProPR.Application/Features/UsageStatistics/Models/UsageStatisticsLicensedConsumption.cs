// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     What one installation reports about the license it runs under and what it holds against the limits that
///     license states.
///     <para>
///         A license is not bound to an installation, so one license may run on several of them. The total
///         under a license is therefore only readable from what each installation reports, and those reports
///         are grouped by the license identifier together with the licensing identity.
///     </para>
///     <para>
///         The values are read for reporting. No verification, resolution, activation or quota decision reads
///         this type, and a report that is not delivered changes nothing about what the installation is
///         entitled to.
///     </para>
/// </summary>
public sealed record UsageStatisticsLicensedConsumption
{
    /// <summary>The identifier of the verified license, from its <c>jti</c> claim.</summary>
    public required string LicenseId { get; init; }

    /// <summary>
    ///     The identifier the installation reports itself under in licensing. It is a second random value
    ///     beside the usage-statistics instance identifier, and it is the one the licensing panel and the
    ///     licensing API show, so a report can be matched against what an operator reads.
    /// </summary>
    public required Guid LicensingIdentity { get; init; }

    /// <summary>
    ///     The recorded hash of the installation's stable components, or <see langword="null" /> when no
    ///     profile has been captured yet. The recorded value is read rather than computed again here, so the
    ///     reported hash is the one the installation's own profile read returns.
    /// </summary>
    public string? SystemProfileHash { get; init; }

    /// <summary>Clients configured on the installation.</summary>
    public required long Clients { get; init; }

    /// <summary>Runners holding a current credential.</summary>
    public required long EnrolledRunners { get; init; }

    /// <summary>
    ///     The highest number of reviews that executed at the same time on the previous UTC day, or
    ///     <see langword="null" /> when that day recorded none.
    ///     <para>
    ///         Clients and runners are stock: what the installation holds now is what it holds for the day.
    ///         Concurrent reviews rise and fall as work is claimed and finishes, so the number that can be
    ///         compared against the ceiling a license states is the day's highest, and the day reported is the
    ///         last one that has finished.
    ///     </para>
    /// </summary>
    public long? PeakConcurrentReviewsPreviousDay { get; init; }

    /// <summary>
    ///     Distinct authors the current UTC month holds, automation identities left out, or
    ///     <see langword="null" /> when the installation has no author rollup to read.
    ///     <para>
    ///         The current month rather than the last finished one, because the limit a license states for
    ///         authors is a monthly one and the month under way is the one it applies to. The month is still
    ///         running when the report is built, so the number rises across the reports sent within one month
    ///         and the last report of a month carries what that month held.
    ///     </para>
    ///     <para>
    ///         An installation with no rollup reports no number rather than zero, which is how the licensing
    ///         page reports the same dimension. A zero would be indistinguishable from a month in which no
    ///         author was recorded.
    ///     </para>
    /// </summary>
    public long? AuthorsCurrentMonth { get; init; }
}
