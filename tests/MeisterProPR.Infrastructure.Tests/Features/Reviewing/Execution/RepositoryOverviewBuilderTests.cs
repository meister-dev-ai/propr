// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class RepositoryOverviewBuilderTests
{
    [Fact]
    public void Build_ReturnsStructuredRepositorySignals()
    {
        var result = RepositoryOverviewBuilder.Build(
            RepositorySearchBranchSides.Source,
            "feature/test",
            [
                "MeisterProPR.sln",
                "src/MeisterProPR.Api/Program.cs",
                "src/MeisterProPR.Infrastructure/Features/Reviewing/Execution/RepositoryOverviewBuilder.cs",
                "tests/MeisterProPR.Infrastructure.Tests/RepositoryOverviewBuilderTests.cs",
                "src/MeisterProPR.Api/appsettings.json",
                "src/MeisterProPR.Infrastructure/Migrations/Initial.cs",
                "src/MeisterProPR.Infrastructure/DependencyInjection/ReviewingServiceCollectionExtensions.cs",
                "docs/architecture/reviewing-workflows.md",
            ]);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Contains("MeisterProPR.sln", result.Projects.Paths);
        Assert.Contains("src/MeisterProPR.Api/Program.cs", result.EntryPoints.Paths);
        Assert.Contains(result.ModuleBoundaries.Paths, path => path.Contains("Features/Reviewing", StringComparison.Ordinal));
        Assert.Contains("tests/MeisterProPR.Infrastructure.Tests/RepositoryOverviewBuilderTests.cs", result.TestLocations.Paths);
        Assert.Contains("src/MeisterProPR.Api/appsettings.json", result.ConfigTouchpoints.Paths);
        Assert.Contains("src/MeisterProPR.Infrastructure/Migrations/Initial.cs", result.PersistencePaths.Paths);
        Assert.Contains("src/MeisterProPR.Infrastructure/DependencyInjection/ReviewingServiceCollectionExtensions.cs", result.RegistrationLocations.Paths);
        Assert.Contains("docs/architecture/reviewing-workflows.md", result.DocsAndSpecs.Paths);
    }
}
