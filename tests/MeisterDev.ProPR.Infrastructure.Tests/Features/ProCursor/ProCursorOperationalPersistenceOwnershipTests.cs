// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.ProCursor;

public sealed class ProCursorOperationalPersistenceOwnershipTests
{
    [Fact]
    public void ProCursorOperationalPersistence_CompilesFromProCursorAssembly()
    {
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorOperationalDbContext).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorIndexJobRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorIndexSnapshotRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorSymbolGraphRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorTokenUsageReadRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(EfProCursorTokenUsageRecorder).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorTokenUsageAggregationService).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorTokenUsageRebuildService).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor", typeof(ProCursorTokenUsageRetentionService).Assembly.GetName().Name);
    }
}
