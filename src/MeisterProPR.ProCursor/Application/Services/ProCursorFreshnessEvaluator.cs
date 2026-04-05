// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Services;

internal static class ProCursorFreshnessEvaluator
{
    public static string GetSnapshotFreshnessStatus(ProCursorTrackedBranch? trackedBranch, ProCursorIndexSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "missing";
        }

        if (string.Equals(snapshot.Status, "building", StringComparison.OrdinalIgnoreCase))
        {
            return "building";
        }

        if (string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (trackedBranch is null)
        {
            return string.Equals(snapshot.Status, "ready", StringComparison.OrdinalIgnoreCase)
                ? "fresh"
                : snapshot.Status;
        }

        if (!string.IsNullOrWhiteSpace(trackedBranch.LastSeenCommitSha) &&
            !string.Equals(trackedBranch.LastSeenCommitSha, snapshot.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return "stale";
        }

        if (!string.IsNullOrWhiteSpace(trackedBranch.LastIndexedCommitSha) &&
            string.Equals(trackedBranch.LastIndexedCommitSha, snapshot.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return "fresh";
        }

        return string.Equals(snapshot.Status, "ready", StringComparison.OrdinalIgnoreCase)
            ? "fresh"
            : snapshot.Status;
    }

    public static string GetBranchFreshnessStatus(ProCursorTrackedBranch trackedBranch, ProCursorIndexSnapshot? latestSnapshot)
    {
        if (latestSnapshot is not null &&
            string.Equals(latestSnapshot.Status, "building", StringComparison.OrdinalIgnoreCase))
        {
            return "building";
        }

        if (latestSnapshot is not null &&
            string.Equals(latestSnapshot.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(latestSnapshot.CommitSha, trackedBranch.LastSeenCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (string.IsNullOrWhiteSpace(trackedBranch.LastIndexedCommitSha))
        {
            return "missing";
        }

        if (!string.IsNullOrWhiteSpace(trackedBranch.LastSeenCommitSha) &&
            !string.Equals(trackedBranch.LastSeenCommitSha, trackedBranch.LastIndexedCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return "stale";
        }

        return "fresh";
    }
}
