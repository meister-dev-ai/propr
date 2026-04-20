// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Entities.ProCursor;

public sealed class ProCursorTrackedBranchTransitionTests
{
    private static ProCursorTrackedBranch CreateTrackedBranch()
    {
        return new ProCursorTrackedBranch(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "main",
            ProCursorRefreshTriggerMode.Manual,
            true);
    }

    [Fact]
    public void Constructor_DefaultsBranchToEnabled()
    {
        var branch = CreateTrackedBranch();

        Assert.True(branch.IsEnabled);
        Assert.Equal(ProCursorRefreshTriggerMode.Manual, branch.RefreshTriggerMode);
        Assert.True(branch.MiniIndexEnabled);
        Assert.Null(branch.LastSeenCommitSha);
        Assert.Null(branch.LastIndexedCommitSha);
    }

    [Fact]
    public void UpdateSettings_UpdatesModeMiniIndexAndEnabled()
    {
        var branch = CreateTrackedBranch();

        branch.UpdateSettings(ProCursorRefreshTriggerMode.BranchUpdate, false, false);

        Assert.Equal(ProCursorRefreshTriggerMode.BranchUpdate, branch.RefreshTriggerMode);
        Assert.False(branch.MiniIndexEnabled);
        Assert.False(branch.IsEnabled);
    }

    [Fact]
    public void RecordSeenCommit_SetsLastSeenCommit()
    {
        var branch = CreateTrackedBranch();

        branch.RecordSeenCommit("abc123");

        Assert.Equal("abc123", branch.LastSeenCommitSha);
        Assert.Null(branch.LastIndexedCommitSha);
    }

    [Fact]
    public void RecordIndexedCommit_WhenLastSeenIsMissing_SetsBothCommitFields()
    {
        var branch = CreateTrackedBranch();

        branch.RecordIndexedCommit("def456");

        Assert.Equal("def456", branch.LastSeenCommitSha);
        Assert.Equal("def456", branch.LastIndexedCommitSha);
    }

    [Fact]
    public void SetEnabled_UpdatesEnabledState()
    {
        var branch = CreateTrackedBranch();

        branch.SetEnabled(false);

        Assert.False(branch.IsEnabled);
    }
}
