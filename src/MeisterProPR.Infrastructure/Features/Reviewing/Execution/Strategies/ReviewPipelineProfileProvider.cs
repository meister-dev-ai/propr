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
    public const string FileByFileBaselineProfileId = "file-by-file-baseline";
    public const string AgenticBaselineProfileId = "agentic-baseline";
    public const string AgenticExperimentalProfileId = "agentic-experimental";
    public const string PrWideBaselineProfileId = "pr-wide-baseline";

    private static readonly IReadOnlyDictionary<ReviewStrategy, IReadOnlyList<ReviewPipelineProfile>> Profiles =
        new Dictionary<ReviewStrategy, IReadOnlyList<ReviewPipelineProfile>>
        {
            [ReviewStrategy.FileByFile] =
            [
                new ReviewPipelineProfile(
                    FileByFileBaselineProfileId,
                    "File-by-file baseline",
                    ReviewStrategy.FileByFile,
                    [DispatchStageFamilyId],
                    [
                        FileByFileConfidenceFloorStage.StageIdConstant,
                        FileByFileSpeculativeCommentFilterStage.StageIdConstant,
                        FileByFileInfoCommentStripStage.StageIdConstant,
                        FileByFileVagueSuggestionFilterStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    true),
            ],
            [ReviewStrategy.AgenticFileByFile] =
            [
                new ReviewPipelineProfile(
                    AgenticBaselineProfileId,
                    "Agentic baseline",
                    ReviewStrategy.AgenticFileByFile,
                    [DispatchStageFamilyId],
                    [
                        AgenticConfidenceFloorStage.StageIdConstant,
                        AgenticSpeculativeCommentFilterStage.StageIdConstant,
                        AgenticInfoCommentStripStage.StageIdConstant,
                        AgenticVagueSuggestionFilterStage.StageIdConstant,
                    ],
                    [FinalizeStageFamilyId],
                    true),
                new ReviewPipelineProfile(
                    AgenticExperimentalProfileId,
                    "Agentic experimental",
                    ReviewStrategy.AgenticFileByFile,
                    [DispatchStageFamilyId],
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
                    PrWideBaselineProfileId,
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
