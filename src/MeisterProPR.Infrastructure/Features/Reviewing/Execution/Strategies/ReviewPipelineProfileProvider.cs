// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
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
    public const string AgenticBaselineProfileId = ReviewPipelineProfileCatalog.AgenticBaselineProfileId;
    public const string AgenticExperimentalProfileId = ReviewPipelineProfileCatalog.AgenticExperimentalProfileId;
    public const string PrWideBaselineProfileId = ReviewPipelineProfileCatalog.PrWideBaselineProfileId;

    private static readonly IReadOnlyDictionary<ReviewStrategy, IReadOnlyList<ReviewPipelineProfile>> Profiles =
        new Dictionary<ReviewStrategy, IReadOnlyList<ReviewPipelineProfile>>
        {
            [ReviewStrategy.FileByFile] =
            [
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.FileByFileBaselineProfileId,
                    "File-by-file baseline",
                    ReviewStrategy.FileByFile,
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
                    ReviewStrategy.FileByFile,
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
                    ReviewStrategy.FileByFile,
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
                    ReviewStrategy.FileByFile,
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
            ],
            [ReviewStrategy.AgenticFileByFile] =
            [
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.AgenticBaselineProfileId,
                    "Agentic baseline",
                    ReviewStrategy.AgenticFileByFile,
                    [],
                    [
                        AgenticConfidenceFloorStage.StageIdConstant,
                        FileByFileSemanticScreeningStage.StageIdConstant,
                        AgenticInfoCommentStripStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    true),
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.AgenticExperimentalProfileId,
                    "Agentic experimental",
                    ReviewStrategy.AgenticFileByFile,
                    [],
                    [
                        FileByFileSemanticScreeningStage.StageIdConstant,
                        AgenticInfoCommentStripStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    false),
            ],
            [ReviewStrategy.PrWideAgentic] =
            [
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.PrWideBaselineProfileId,
                    "PR-wide baseline",
                    ReviewStrategy.PrWideAgentic,
                    [DispatchStageFamilyId],
                    [],
                    [FinalizeStageFamilyId],
                    true),
            ],
        };

    public IReadOnlyList<ReviewPipelineProfile> GetProfiles(ReviewStrategy strategy)
    {
        return Profiles.TryGetValue(strategy, out var profiles) ? profiles : [];
    }
}
