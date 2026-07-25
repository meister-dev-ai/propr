// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class FileNeighborhoodBuilderTests
{
    [Fact]
    public void Build_ReturnsFocusedNeighborhoodForExistingFile()
    {
        var result = FileNeighborhoodBuilder.Build(
            RepositorySearchBranchSides.Source,
            "feature/test",
            "src/MeisterDev.ProPR.Application/Features/Reviewing/Foo.cs",
            [
                "src/MeisterDev.ProPR.Application/MeisterDev.ProPR.Application.csproj",
                "src/MeisterDev.ProPR.Application/Features/Reviewing/Foo.cs",
                "tests/MeisterDev.ProPR.Application.Tests/Features/Reviewing/FooTests.cs",
                "src/MeisterDev.ProPR.Application/appsettings.json",
                "src/MeisterDev.ProPR.Application/DependencyInjection/ReviewingServiceCollectionExtensions.cs",
                "docs/architecture/reviewing-workflows.md",
            ]);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Equal("src/MeisterDev.ProPR.Application/MeisterDev.ProPR.Application.csproj", result.OwningProjectOrModule);
        Assert.Contains("tests/MeisterDev.ProPR.Application.Tests/Features/Reviewing/FooTests.cs", result.NearbyTests);
        Assert.Contains("src/MeisterDev.ProPR.Application/appsettings.json", result.ConfigTouchpoints);
        Assert.Contains("src/MeisterDev.ProPR.Application/DependencyInjection/ReviewingServiceCollectionExtensions.cs", result.RegistrationLocations);
        Assert.Contains("docs/architecture/reviewing-workflows.md", result.DocsAndSpecs);
    }

    [Fact]
    public void Build_MissingFile_ReturnsStructuredNotFoundLimitation()
    {
        var result = FileNeighborhoodBuilder.Build(
            RepositorySearchBranchSides.Source,
            "feature/test",
            "src/Missing.cs",
            ["src/Other.cs"]);

        Assert.Equal(RepositorySearchStatuses.InvalidRequest, result.Status);
        Assert.Contains(result.Limitations, limitation => limitation.Reason == RepositorySearchLimitationReasons.FileNotFound);
    }
}
