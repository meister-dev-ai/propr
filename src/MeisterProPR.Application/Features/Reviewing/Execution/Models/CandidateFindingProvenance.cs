// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable origin metadata for a final candidate finding.
/// </summary>
public sealed record CandidateFindingProvenance
{
    /// <summary>
    ///     Origin kind used for findings derived from a per-file comment.
    /// </summary>
    public const string PerFileCommentOrigin = "per_file_comment";

    /// <summary>
    ///     Origin kind used for findings synthesized across the full review.
    /// </summary>
    public const string SynthesizedCrossCuttingOrigin = "synthesized_cross_cutting";

    /// <summary>
    ///     Initializes stable provenance metadata for a candidate finding.
    /// </summary>
    /// <param name="originKind">Kind of source that produced the finding.</param>
    /// <param name="generatedByStage">Review stage that generated the finding.</param>
    /// <param name="sourceFilePath">Optional repository-relative source file path.</param>
    /// <param name="sourceFileResultId">Optional review file result identifier.</param>
    /// <param name="sourceCommentOrdinal">Optional ordinal of the originating comment within the file result.</param>
    public CandidateFindingProvenance(
        string originKind,
        string generatedByStage,
        string? sourceFilePath = null,
        Guid? sourceFileResultId = null,
        int? sourceCommentOrdinal = null)
    {
        if (string.IsNullOrWhiteSpace(originKind))
        {
            throw new ArgumentException("Origin kind is required.", nameof(originKind));
        }

        if (string.IsNullOrWhiteSpace(generatedByStage))
        {
            throw new ArgumentException("Generated-by stage is required.", nameof(generatedByStage));
        }

        this.OriginKind = originKind;
        this.GeneratedByStage = generatedByStage;
        this.SourceFilePath = sourceFilePath;
        this.SourceFileResultId = sourceFileResultId;
        this.SourceCommentOrdinal = sourceCommentOrdinal;
    }

    /// <summary>
    ///     Gets the kind of origin that produced the finding.
    /// </summary>
    public string OriginKind { get; }

    /// <summary>
    ///     Gets the review stage that generated the finding.
    /// </summary>
    public string GeneratedByStage { get; }

    /// <summary>
    ///     Gets the repository-relative source file path when the finding originated from a file result.
    /// </summary>
    public string? SourceFilePath { get; }

    /// <summary>
    ///     Gets the source file-result identifier when available.
    /// </summary>
    public Guid? SourceFileResultId { get; }

    /// <summary>
    ///     Gets the ordinal of the originating comment within the source file result when available.
    /// </summary>
    public int? SourceCommentOrdinal { get; }
}
