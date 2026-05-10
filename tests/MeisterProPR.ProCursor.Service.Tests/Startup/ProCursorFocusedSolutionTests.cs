// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.ProCursor.Service.Tests.Startup;

public sealed class ProCursorFocusedSolutionTests
{
    [Fact]
    public void FocusedSolution_ContainsOnlyProCursorOwnedProjectsAndSharedContracts()
    {
        var repoRoot = ResolveRepoRoot();
        var solutionContents = File.ReadAllText(Path.Combine(repoRoot, "MeisterProPR.ProCursor.slnx"));

        Assert.Contains("src/MeisterProPR.ProCursor.Contracts/MeisterProPR.ProCursor.Contracts.csproj", solutionContents, StringComparison.Ordinal);
        Assert.Contains("src/MeisterProPR.ProCursor/MeisterProPR.ProCursor.csproj", solutionContents, StringComparison.Ordinal);
        Assert.Contains("src/MeisterProPR.ProCursor.Service/MeisterProPR.ProCursor.Service.csproj", solutionContents, StringComparison.Ordinal);
        Assert.DoesNotContain("src/MeisterProPR.Application/MeisterProPR.Application.csproj", solutionContents, StringComparison.Ordinal);
        Assert.DoesNotContain("src/MeisterProPR.Application/MeisterProPR.Application.csproj", solutionContents, StringComparison.Ordinal);
        Assert.DoesNotContain("src/MeisterProPR.Infrastructure/MeisterProPR.Infrastructure.csproj", solutionContents, StringComparison.Ordinal);
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
