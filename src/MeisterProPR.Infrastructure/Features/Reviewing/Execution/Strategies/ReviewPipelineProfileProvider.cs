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
                        FileByFileProRvPrefilterStage.StageIdConstant,
                    ],
                    [
                        FileByFileConfidenceFloorStage.StageIdConstant,
                        FileByFileSpeculativeCommentFilterStage.StageIdConstant,
                        FileByFileInfoCommentStripStage.StageIdConstant,
                        FileByFileVagueSuggestionFilterStage.StageIdConstant,
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
                        FileByFileProRvPrefilterStage.StageIdConstant,
                    ],
                    [
                        FileByFileConfidenceFloorStage.StageIdConstant,
                        FileByFileSpeculativeCommentFilterStage.StageIdConstant,
                        FileByFileInfoCommentStripStage.StageIdConstant,
                        FileByFileVagueSuggestionFilterStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    false),
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.FileByFileBalancedProfileId,
                    "Balanced",
                    ReviewStrategy.FileByFile,
                    [
                        FileByFileContextPrefetchStage.StageIdConstant,
                        FileByFileRiskMarkerStage.StageIdConstant,
                        FileByFileProRvPrefilterStage.StageIdConstant,
                    ],
                    [
                        FileByFileConfidenceFloorStage.StageIdConstant,
                        FileByFileInfoCommentStripStage.StageIdConstant,
                        FileByFileImportanceRankingStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    true),
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId,
                    "Assertive",
                    ReviewStrategy.FileByFile,
                    [
                        FileByFileContextPrefetchStage.StageIdConstant,
                        FileByFileRiskMarkerStage.StageIdConstant,
                        FileByFileProRvPrefilterStage.StageIdConstant,
                    ],
                    [
                        FileByFileInfoCommentStripStage.StageIdConstant,
                        FileByFileImportanceRankingStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    false),
            ],
            [ReviewStrategy.AgenticFileByFile] =
            [
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.AgenticBaselineProfileId,
                    "Agentic baseline",
                    ReviewStrategy.AgenticFileByFile,
                    [AgenticProRvPrefilterStage.StageIdConstant],
                    [
                        AgenticConfidenceFloorStage.StageIdConstant,
                        AgenticSpeculativeCommentFilterStage.StageIdConstant,
                        AgenticInfoCommentStripStage.StageIdConstant,
                        AgenticVagueSuggestionFilterStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    true),
                new ReviewPipelineProfile(
                    ReviewPipelineProfileCatalog.AgenticExperimentalProfileId,
                    "Agentic experimental",
                    ReviewStrategy.AgenticFileByFile,
                    [AgenticProRvPrefilterStage.StageIdConstant],
                    [
                        AgenticSpeculativeCommentFilterStage.StageIdConstant,
                        AgenticInfoCommentStripStage.StageIdConstant,
                        AgenticVagueSuggestionFilterStage.StageIdConstant,
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
