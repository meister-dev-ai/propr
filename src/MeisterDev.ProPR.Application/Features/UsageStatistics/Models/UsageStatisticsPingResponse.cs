// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     The receiver's response, which carries the version and advisory information shown to operators.
///     <para>
///         Every field is optional. The sender treats the whole response as advisory: an empty object, fields
///         this build does not know, and no response at all are all handled the same way.
///     </para>
/// </summary>
public sealed record UsageStatisticsPingResponse
{
    /// <summary>The schema the response follows, when the receiver states one.</summary>
    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; init; }

    /// <summary>The newest published release, when the receiver knows one.</summary>
    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; init; }

    /// <summary>Advisories that apply to the reported version.</summary>
    [JsonPropertyName("advisories")]
    public IReadOnlyList<ProductAdvisory> Advisories { get; init; } = [];
}
