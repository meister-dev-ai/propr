// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Domain.Tests.Entities.ProCursor;

public sealed class ProCursorIndexSnapshotLifecycleTests
{
    private static ProCursorIndexSnapshot CreateSnapshot()
    {
        return new ProCursorIndexSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "abc123", "full");
    }

    [Fact]
    public void Constructor_DefaultsStatusToBuilding()
    {
        var snapshot = CreateSnapshot();

        Assert.Equal("building", snapshot.Status);
        Assert.Equal(0, snapshot.FileCount);
        Assert.Equal(0, snapshot.ChunkCount);
        Assert.Equal(0, snapshot.SymbolCount);
        Assert.False(snapshot.SupportsSymbolQueries);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void MarkReady_FromBuilding_SetsReadyStateAndCounts()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var snapshot = CreateSnapshot();

        snapshot.MarkReady(10, 25, 7, true);

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.Equal("ready", snapshot.Status);
        Assert.Equal(10, snapshot.FileCount);
        Assert.Equal(25, snapshot.ChunkCount);
        Assert.Equal(7, snapshot.SymbolCount);
        Assert.True(snapshot.SupportsSymbolQueries);
        Assert.InRange(snapshot.CompletedAt!.Value, before, after);
    }

    [Fact]
    public void MarkFailed_FromBuilding_SetsFailedState()
    {
        var snapshot = CreateSnapshot();

        snapshot.MarkFailed("materialization failed");

        Assert.Equal("failed", snapshot.Status);
        Assert.Equal("materialization failed", snapshot.FailureReason);
        Assert.NotNull(snapshot.CompletedAt);
    }

    [Fact]
    public void MarkSuperseded_FromReady_SetsSupersededState()
    {
        var snapshot = CreateSnapshot();
        snapshot.MarkReady(1, 1, 1, false);

        snapshot.MarkSuperseded();

        Assert.Equal("superseded", snapshot.Status);
        Assert.NotNull(snapshot.CompletedAt);
    }

    [Fact]
    public void MarkSuperseded_FromBuilding_Throws()
    {
        var snapshot = CreateSnapshot();

        Assert.Throws<InvalidOperationException>(snapshot.MarkSuperseded);
    }
}
