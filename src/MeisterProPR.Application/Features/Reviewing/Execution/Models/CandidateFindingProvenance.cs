// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable origin metadata for a final candidate finding.
/// </summary>
public sealed record CandidateFindingProvenance
{
    public const string PerFileCommentOrigin = "per_file_comment";
    public const string SynthesizedCrossCuttingOrigin = "synthesized_cross_cutting";

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

    public string OriginKind { get; }

    public string GeneratedByStage { get; }

    public string? SourceFilePath { get; }

    public Guid? SourceFileResultId { get; }

    public int? SourceCommentOrdinal { get; }
}
