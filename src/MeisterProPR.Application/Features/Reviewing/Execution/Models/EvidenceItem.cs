// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One concrete evidence input collected for a verification claim.
/// </summary>
public sealed record EvidenceItem
{
    public EvidenceItem(
        string kind,
        string summary,
        string? sourceId = null,
        string? payloadReference = null,
        string? freshnessState = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Evidence kind is required.", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Evidence summary is required.", nameof(summary));
        }

        this.Kind = kind;
        this.SourceId = sourceId;
        this.Summary = summary;
        this.PayloadReference = payloadReference;
        this.FreshnessState = freshnessState;
    }

    public string Kind { get; }

    public string? SourceId { get; }

    public string Summary { get; }

    public string? PayloadReference { get; }

    public string? FreshnessState { get; }
}
