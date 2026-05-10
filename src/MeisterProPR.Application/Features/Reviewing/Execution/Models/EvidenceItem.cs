// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One concrete evidence input collected for a verification claim.
/// </summary>
public sealed record EvidenceItem
{
    /// <summary>
    ///     Initializes a concrete evidence item.
    /// </summary>
    /// <param name="kind">Machine-readable kind of evidence.</param>
    /// <param name="summary">Human-readable summary of the evidence.</param>
    /// <param name="sourceId">Optional source identifier for the evidence.</param>
    /// <param name="payloadReference">Optional payload reference for detailed evidence data.</param>
    /// <param name="freshnessState">Optional freshness state for the evidence.</param>
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

    /// <summary>
    ///     Gets the machine-readable kind of evidence.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    ///     Gets the source identifier for the evidence when available.
    /// </summary>
    public string? SourceId { get; }

    /// <summary>
    ///     Gets the human-readable summary of the evidence.
    /// </summary>
    public string Summary { get; }

    /// <summary>
    ///     Gets the payload reference for detailed evidence data when available.
    /// </summary>
    public string? PayloadReference { get; }

    /// <summary>
    ///     Gets the freshness state for the evidence when available.
    /// </summary>
    public string? FreshnessState { get; }
}
