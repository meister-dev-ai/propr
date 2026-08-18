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
///         Every counter is a bucket label rather than a number. Bucketing happens while the snapshot is built,
///         so a raw count never reaches serialization and never leaves the installation.
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
}
