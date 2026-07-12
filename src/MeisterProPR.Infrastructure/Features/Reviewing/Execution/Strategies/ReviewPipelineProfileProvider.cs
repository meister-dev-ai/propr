// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;

internal sealed class ReviewPipelineProfileProvider : IReviewPipelineProfileProvider
{
    // These identifiers are persisted and emitted through protocol/selection flows.
    // Treat renames as compatibility changes rather than local refactors.
    public const string DispatchStageFamilyId = "dispatch";
    public const string FinalizeStageFamilyId = "finalize";
    public const string FileByFileBaselineProfileId = ReviewPipelineProfileCatalog.FileByFileBaselineProfileId;
    public const string FileByFileCalmProfileId = ReviewPipelineProfileCatalog.FileByFileCalmProfileId;
    public const string FileByFileBalancedProfileId = ReviewPipelineProfileCatalog.FileByFileBalancedProfileId;
    public const string FileByFileAssertiveProfileId = ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId;

    private static readonly IReadOnlyList<ReviewPipelineProfile> FileByFileProfiles =
    [
        new ReviewPipelineProfile(
            ReviewPipelineProfileCatalog.FileByFileBaselineProfileId,
            "File-by-file baseline",
            [
                FileByFileContextPrefetchStage.StageIdConstant,
                FileByFileRiskMarkerStage.StageIdConstant,
            ],
            [
                FileByFileConfidenceFloorStage.StageIdConstant,
                FileByFileSemanticScreeningStage.StageIdConstant,
                FileByFileInfoCommentStripStage.StageIdConstant,
            ],
            [FinalizeStageFamilyId],
            false),
        new ReviewPipelineProfile(
            ReviewPipelineProfileCatalog.FileByFileCalmProfileId,
            "Calm",
            [
                FileByFileContextPrefetchStage.StageIdConstant,
                FileByFileRiskMarkerStage.StageIdConstant,
            ],
            [
                FileByFileConfidenceFloorStage.StageIdConstant,
                FileByFileSemanticScreeningStage.StageIdConstant,
                FileByFileInfoCommentStripStage.StageIdConstant,
            ],
            [FinalizeStageFamilyId],
            false,
            ReviewAggressiveness.Calm),
        new ReviewPipelineProfile(
            ReviewPipelineProfileCatalog.FileByFileBalancedProfileId,
            "Balanced",
            [
                FileByFileContextPrefetchStage.StageIdConstant,
                FileByFileRiskMarkerStage.StageIdConstant,
            ],
            [
                FileByFileConfidenceFloorStage.StageIdConstant,
                FileByFileSemanticScreeningStage.StageIdConstant,
                FileByFileInfoCommentStripStage.StageIdConstant,
                FileByFileSelfReflectionRankingStage.StageIdConstant,
            ],
            [FinalizeStageFamilyId],
            true,
            ReviewAggressiveness.Balanced,
            10),
        new ReviewPipelineProfile(
            ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId,
            "Assertive",
            [
                FileByFileContextPrefetchStage.StageIdConstant,
                FileByFileRiskMarkerStage.StageIdConstant,
            ],
            [
                FileByFileSemanticScreeningStage.StageIdConstant,
                FileByFileInfoCommentStripStage.StageIdConstant,
                FileByFileSelfReflectionRankingStage.StageIdConstant,
            ],
            [FinalizeStageFamilyId],
            false,
            ReviewAggressiveness.Assertive,
            1),
    ];

    public IReadOnlyList<ReviewPipelineProfile> GetProfiles()
    {
        return FileByFileProfiles;
    }
}
