// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Polls tracked branches for head changes and queues durable refresh jobs when required.
/// </summary>
public sealed partial class ProCursorRefreshScheduler(
    IProCursorKnowledgeSourceRepository knowledgeSourceRepository,
    IProCursorTrackedBranchChangeDetector changeDetector,
    ProCursorIndexCoordinator indexCoordinator,
    ILogger<ProCursorRefreshScheduler> logger)
{
    /// <summary>
    ///     Schedules refresh work for tracked branches whose observed head has advanced.
    /// </summary>
    public async Task<int> ScheduleRefreshesAsync(CancellationToken ct = default)
    {
        var queuedCount = 0;
        var sources = await knowledgeSourceRepository.ListEnabledAsync(ct);

        foreach (var source in sources)
        {
            foreach (var trackedBranch in source.TrackedBranches
                         .Where(branch =>
                             branch.IsEnabled && branch.RefreshTriggerMode == ProCursorRefreshTriggerMode.BranchUpdate)
                         .OrderBy(branch => branch.BranchName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var latestCommitSha = await changeDetector.GetLatestCommitShaAsync(source, trackedBranch, ct);
                    if (string.IsNullOrWhiteSpace(latestCommitSha))
                    {
                        continue;
                    }

                    var normalizedCommitSha = latestCommitSha.Trim();
                    if (string.Equals(
                            trackedBranch.LastIndexedCommitSha,
                            normalizedCommitSha,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(
                            trackedBranch.LastSeenCommitSha,
                            normalizedCommitSha,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        trackedBranch.RecordSeenCommit(normalizedCommitSha);
                        await knowledgeSourceRepository.UpdateAsync(source, ct);
                    }

                    await indexCoordinator.QueueRefreshAsync(
                        source.ClientId,
                        source.Id,
                        new ProCursorRefreshRequest(
                            trackedBranch.Id,
                            normalizedCommitSha),
                        ct);
                    queuedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRefreshPollFailed(logger, source.Id, trackedBranch.Id, trackedBranch.BranchName, ex);
                }
            }
        }

        return queuedCount;
    }
}
