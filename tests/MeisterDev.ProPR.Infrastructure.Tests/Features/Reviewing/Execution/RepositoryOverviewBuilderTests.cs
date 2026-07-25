// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class RepositoryOverviewBuilderTests
{
    [Fact]
    public void Build_ReturnsStructuredRepositorySignals()
    {
        var result = RepositoryOverviewBuilder.Build(
            RepositorySearchBranchSides.Source,
            "feature/test",
            [
                "MeisterDev.ProPR.sln",
                "src/MeisterDev.ProPR.Api/Program.cs",
                "src/MeisterDev.ProPR.Infrastructure/Features/Reviewing/Execution/RepositoryOverviewBuilder.cs",
                "tests/MeisterDev.ProPR.Infrastructure.Tests/RepositoryOverviewBuilderTests.cs",
                "src/MeisterDev.ProPR.Api/appsettings.json",
                "src/MeisterDev.ProPR.Infrastructure/Migrations/Initial.cs",
                "src/MeisterDev.ProPR.Infrastructure/DependencyInjection/ReviewingServiceCollectionExtensions.cs",
                "docs/architecture/reviewing-workflows.md",
            ]);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Contains("MeisterDev.ProPR.sln", result.Projects.Paths);
        Assert.Contains("src/MeisterDev.ProPR.Api/Program.cs", result.EntryPoints.Paths);
        Assert.Contains(result.ModuleBoundaries.Paths, path => path.Contains("Features/Reviewing", StringComparison.Ordinal));
        Assert.Contains("tests/MeisterDev.ProPR.Infrastructure.Tests/RepositoryOverviewBuilderTests.cs", result.TestLocations.Paths);
        Assert.Contains("src/MeisterDev.ProPR.Api/appsettings.json", result.ConfigTouchpoints.Paths);
        Assert.Contains("src/MeisterDev.ProPR.Infrastructure/Migrations/Initial.cs", result.PersistencePaths.Paths);
        Assert.Contains("src/MeisterDev.ProPR.Infrastructure/DependencyInjection/ReviewingServiceCollectionExtensions.cs", result.RegistrationLocations.Paths);
        Assert.Contains("docs/architecture/reviewing-workflows.md", result.DocsAndSpecs.Paths);
    }
}
