// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     The complete set of values an installation sends about itself.
///     <para>
///         The same type serves as the wire payload, as the body of the administrator's payload preview, and as
///         the subject of the contract test that checks the published payload documentation. Nothing is sent
///         that is not a property here, so a field added without a documentation entry fails the build.
///     </para>
///     <para>
///         Every activity counter is a bucket label rather than a number. Bucketing happens while the snapshot
///         is built, so a raw activity count never reaches serialization and never leaves the installation.
///     </para>
///     <para>
///         The licensing fields are carried only by an installation running a commercial license, and they are
///         absent rather than null on every other one. They are additive and optional on the wire in both
///         directions: a receiver that does not know one ignores it, and a sender with none to report sends the
///         payload shape it sent before these fields existed.
///     </para>
/// </summary>
public sealed record UsageStatisticsSnapshot
{
    /// <summary>The wire schema this payload follows. The schema is additive only, so old senders stay valid.</summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>A random identifier for this installation, generated locally and derived from no other value.</summary>
    [JsonPropertyName("instanceId")]
    public required Guid InstanceId { get; init; }

    /// <summary>The running release version.</summary>
    [JsonPropertyName("productVersion")]
    public required string ProductVersion { get; init; }

    /// <summary>Whether a commercial license is installed.</summary>
    [JsonPropertyName("edition")]
    public required UsageStatisticsEdition Edition { get; init; }

    /// <summary>Bucketed count of user accounts that can currently sign in.</summary>
    [JsonPropertyName("activeUsers")]
    public required string ActiveUsers { get; init; }

    /// <summary>Bucketed count of pull requests reviewed, normalised to one week.</summary>
    [JsonPropertyName("pullRequestsPerWeek")]
    public required string PullRequestsPerWeek { get; init; }

    /// <summary>Bucketed count of findings posted on pull requests, normalised to one week.</summary>
    [JsonPropertyName("findingsRaisedPerWeek")]
    public required string FindingsRaisedPerWeek { get; init; }

    /// <summary>
    ///     Bucketed count of findings the author addressed or acknowledged, normalised to one week, or
    ///     <see langword="null" /> when this installation records no finding outcomes.
    /// </summary>
    [JsonPropertyName("findingsAcceptedPerWeek")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FindingsAcceptedPerWeek { get; init; }

    /// <summary>
    ///     Bucketed count of findings the author dismissed, normalised to one week, or <see langword="null" />
    ///     when this installation records no finding outcomes.
    /// </summary>
    [JsonPropertyName("findingsDismissedPerWeek")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FindingsDismissedPerWeek { get; init; }

    /// <summary>
    ///     The identifier of the license this installation runs under, from the verified document's <c>jti</c>
    ///     claim, or <see langword="null" /> on an installation with no license in force. The identifier
    ///     belongs to a document the vendor issued to a named licensee, so an installation that sends it is
    ///     identifying which licensee it belongs to.
    /// </summary>
    [JsonPropertyName("licenseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LicenseId { get; init; }

    /// <summary>
    ///     The identifier this installation reports itself under in licensing, or <see langword="null" /> on an
    ///     installation with no license in force. It distinguishes the installations that share one license,
    ///     and it is the value the licensing panel and the licensing API show.
    /// </summary>
    [JsonPropertyName("licensingIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LicensingIdentity { get; init; }

    /// <summary>
    ///     The recorded hash of the installation's stable components, in lower-case hexadecimal, or
    ///     <see langword="null" /> on an installation with no license in force and on one whose profile has not
    ///     been captured yet. The components it covers are hashed before the hash is taken, so no host name,
    ///     organization address or database name travels with it.
    /// </summary>
    [JsonPropertyName("systemProfileHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemProfileHash { get; init; }

    /// <summary>
    ///     Clients configured on the installation, against the limit a license states for them, or
    ///     <see langword="null" /> on an installation with no license in force.
    /// </summary>
    [JsonPropertyName("consumedClients")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConsumedClients { get; init; }

    /// <summary>
    ///     Runners holding a current credential, against the limit a license states for them, or
    ///     <see langword="null" /> on an installation with no license in force.
    /// </summary>
    [JsonPropertyName("consumedRunners")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConsumedRunners { get; init; }

    /// <summary>
    ///     The highest number of reviews that executed at the same time on the previous UTC day, against the
    ///     limit a license states for them. Absent on an installation with no license in force, and on one
    ///     whose previous day recorded no executing review.
    ///     <para>
    ///         The two counts above are stock: an installation holds the same clients and runners all day, so
    ///         a reading taken when the snapshot is built describes the day. Reviews executing at the same
    ///         time rise and fall as work is claimed and finishes, so a reading at snapshot time would
    ///         describe the moment the snapshot was built, and two installations reporting at different hours
    ///         could not be compared. The day's highest is reported instead, from the last day that has
    ///         finished.
    ///     </para>
    /// </summary>
    [JsonPropertyName("peakConcurrentReviews")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PeakConcurrentReviews { get; init; }

    /// <summary>
    ///     Distinct authors the current UTC month holds, against the limit a license states for them. Absent
    ///     on an installation with no license in force, and on one that keeps no author rollup to read.
    ///     <para>
    ///         The automation identities the product recognizes are left out of the count, so an installation
    ///         whose pull requests are opened by a dependency updater does not count that account as an
    ///         author. The month is the one under way rather than the last finished one, because the limit is
    ///         stated per month and the month under way is the one it applies to, so the number rises across
    ///         the reports sent within one month.
    ///     </para>
    ///     <para>
    ///         Authors are the one licensed dimension no installation refuses work against, so this report is
    ///         the only channel on which the number leaves the installation that measured it.
    ///     </para>
    /// </summary>
    [JsonPropertyName("consumedAuthorsPerMonth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConsumedAuthorsPerMonth { get; init; }
}
