// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Tests.Features.ProCursor;

public sealed class ProCursorOperationalPersistenceOwnershipTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void ProCursorOperationalPersistence_CompilesFromProCursorAssembly()
    {
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorOperationalDbContext).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorIndexJobRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorIndexSnapshotRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorSymbolGraphRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorTokenUsageReadRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(EfProCursorTokenUsageRecorder).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorTokenUsageAggregationService).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorTokenUsageRebuildService).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor", typeof(ProCursorTokenUsageRetentionService).Assembly.GetName().Name);
    }

    [Fact]
    public void ProPrControlPlaneContext_DoesNotOwnOperationalProCursorTables()
    {
        var contextContents = File.ReadAllText(Path.Combine(RepoRoot, "src/MeisterProPR.Infrastructure/Data/MeisterProPRDbContext.cs"));

        Assert.DoesNotContain("DbSet<ProCursorIndexJob>", contextContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<ProCursorIndexSnapshot>", contextContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<ProCursorKnowledgeChunk>", contextContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<ProCursorSymbolRecord>", contextContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<ProCursorSymbolEdge>", contextContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<ProCursorTokenUsageEvent>", contextContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<ProCursorTokenUsageRollup>", contextContents, StringComparison.Ordinal);
    }

    [Fact]
    public void ProCursorOperationalPersistence_SourceFiles_NoLongerLiveUnderInfrastructure()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Infrastructure/Data/ProCursorOperationalDbContext.cs")));
        Assert.False(
            File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Infrastructure/Features/UsageReporting/Persistence/ProCursorTokenUsageReadRepository.cs")));
        Assert.False(
            File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Infrastructure/Features/UsageReporting/Persistence/EfProCursorTokenUsageRecorder.cs")));
        Assert.False(
            File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Infrastructure/Features/UsageReporting/Services/ProCursorTokenUsageAggregationService.cs")));
        Assert.False(
            File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Infrastructure/Features/UsageReporting/Services/ProCursorTokenUsageRebuildService.cs")));
        Assert.False(
            File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Infrastructure/Features/UsageReporting/Services/ProCursorTokenUsageRetentionService.cs")));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MeisterProPR.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }
}
