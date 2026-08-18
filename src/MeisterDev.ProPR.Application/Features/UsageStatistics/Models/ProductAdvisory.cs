// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>One security advisory, as it arrives in the response to a usage-statistics ping.</summary>
public sealed record ProductAdvisory
{
    /// <summary>Stable identifier for the advisory.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Severity as the vendor published it.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>Short headline for the advisory.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The versions the advisory applies to.</summary>
    [JsonPropertyName("affectedVersions")]
    public string? AffectedVersions { get; init; }

    /// <summary>A link to the advisory details.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }
}
