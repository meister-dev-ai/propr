// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Api.Tests.Workers;

public sealed class RetentionPurgeWorkerTests
{
    [Fact]
    public void DecideForConnection_BothTogglesOff_PurgesEverythingForConnection()
    {
        var connectionId = Guid.NewGuid();
        var connection = new ClientScmConnectionRetentionDto(connectionId, Guid.NewGuid(), false, false, 90);

        var decision = RetentionPurgeWorker.DecideForConnection(connection, DateTimeOffset.UtcNow);

        Assert.Equal(connectionId, decision.ConnectionId);
        Assert.Equal(RetentionPurgeWorker.RetentionAction.PurgeAllForConnection, decision.Action);
    }

    [Fact]
    public void DecideForConnection_ThreadsEnabled_PurgesExpiredAtConfiguredWindow()
    {
        var connectionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var connection = new ClientScmConnectionRetentionDto(connectionId, Guid.NewGuid(), true, false, 7);

        var decision = RetentionPurgeWorker.DecideForConnection(connection, now);

        Assert.Equal(connectionId, decision.ConnectionId);
        Assert.Equal(RetentionPurgeWorker.RetentionAction.PurgeExpired, decision.Action);
        Assert.Equal(now.AddDays(-7), decision.Cutoff);
    }

    [Fact]
    public void DecideForConnection_DiffsEnabledWithoutWindow_FallsBackToDefaultThirtyDays()
    {
        var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var connection = new ClientScmConnectionRetentionDto(Guid.NewGuid(), Guid.NewGuid(), false, true, null);

        var decision = RetentionPurgeWorker.DecideForConnection(connection, now);

        Assert.Equal(RetentionPurgeWorker.RetentionAction.PurgeExpired, decision.Action);
        Assert.Equal(30, RetentionPurgeWorker.DefaultRetentionDays);
        Assert.Equal(now.AddDays(-RetentionPurgeWorker.DefaultRetentionDays), decision.Cutoff);
    }
}
