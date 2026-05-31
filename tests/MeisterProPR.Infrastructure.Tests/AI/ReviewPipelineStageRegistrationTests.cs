// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Guards against profile/stage drift: every dispatch- and per-file-stage id referenced by a
///     pipeline profile must resolve to a registered <see cref="IReviewPipelineStage{T}" />, otherwise
///     <c>ReviewPipelineRunner</c> throws "No Reviewing pipeline stage is registered for '...'" at
///     runtime (regression: the recall-uplift dispatch/ranking stages were referenced by every
///     file-by-file profile but never registered in DI).
/// </summary>
public sealed class ReviewPipelineStageRegistrationTests
{
    // Strategies whose dispatch/per-file stage lists are concrete (resolved through ReviewPipelineRunner).
    // PrWide uses only family-placeholder ids and is resolved by a different mechanism.
    private static readonly ReviewStrategy[] ConcretePipelineStrategies =
    [
        ReviewStrategy.FileByFile,
        ReviewStrategy.AgenticFileByFile,
    ];

    [Fact]
    public void EveryProfileStageIdResolvesToARegisteredStage()
    {
        var registered = RegisteredPerFileStageIds();
        var provider = new ReviewPipelineProfileProvider();

        // Family placeholders are not concrete stage registrations.
        var familyIds = new HashSet<string>(
            [
                ReviewPipelineProfileProvider.DispatchStageFamilyId,
                ReviewPipelineProfileProvider.FinalizeStageFamilyId,
            ],
            StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var strategy in ConcretePipelineStrategies)
        {
            foreach (var profile in provider.GetProfiles(strategy))
            {
                foreach (var stageId in profile.DispatchStageIds.Concat(profile.PerFileStageIds))
                {
                    if (!familyIds.Contains(stageId) && !registered.Contains(stageId))
                    {
                        missing.Add($"{strategy}/{profile.ProfileId} -> '{stageId}'");
                    }
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "Profile stage ids with no registered IReviewPipelineStage<PerFileReviewContext>: "
            + string.Join("; ", missing));
    }

    [Fact]
    public void RecallUpliftStagesAreRegistered()
    {
        var registered = RegisteredPerFileStageIds();

        Assert.Contains(FileByFileContextPrefetchStage.StageIdConstant, registered);
        Assert.Contains(FileByFileRiskMarkerStage.StageIdConstant, registered);
        Assert.Contains(FileByFileImportanceRankingStage.StageIdConstant, registered);
    }

    private static HashSet<string> RegisteredPerFileStageIds()
    {
        var services = new ServiceCollection().AddReviewingExecution();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(IReviewPipelineStage<PerFileReviewContext>))
            {
                continue;
            }

            var implementationType = descriptor.ImplementationType;
            if (implementationType is null)
            {
                continue;
            }

            var field = implementationType.GetField(
                nameof(FileByFileContextPrefetchStage.StageIdConstant),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if (field?.GetRawConstantValue() is string stageId)
            {
                ids.Add(stageId);
            }
        }

        return ids;
    }
}
