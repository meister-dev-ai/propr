// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Application.Tests.Architecture;

public sealed class ProPrOwnershipBoundaryTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void SharedBrokerAbstractions_CompileFromContractsAssembly()
    {
        Assert.Equal("MeisterProPR.ProCursor.Contracts", typeof(IProCursorScmBroker).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor.Contracts", typeof(IProCursorEmbeddingBroker).Assembly.GetName().Name);
    }

    [Fact]
    public void ProCursorProject_NoLongerContainsProPrOwnedBrokerImplementations()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.ProCursor/Infrastructure/Brokers/LocalProCursorScmBroker.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.ProCursor/Infrastructure/Brokers/LocalProCursorEmbeddingBroker.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.ProCursor/Infrastructure/Repositories/ProCursorKnowledgeSourceRepository.cs")));
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
