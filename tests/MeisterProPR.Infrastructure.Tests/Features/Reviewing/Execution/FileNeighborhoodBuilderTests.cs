// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class FileNeighborhoodBuilderTests
{
    [Fact]
    public void Build_ReturnsFocusedNeighborhoodForExistingFile()
    {
        var result = FileNeighborhoodBuilder.Build(
            RepositorySearchBranchSides.Source,
            "feature/test",
            "src/MeisterProPR.Application/Features/Reviewing/Foo.cs",
            [
                "src/MeisterProPR.Application/MeisterProPR.Application.csproj",
                "src/MeisterProPR.Application/Features/Reviewing/Foo.cs",
                "tests/MeisterProPR.Application.Tests/Features/Reviewing/FooTests.cs",
                "src/MeisterProPR.Application/appsettings.json",
                "src/MeisterProPR.Application/DependencyInjection/ReviewingServiceCollectionExtensions.cs",
                "docs/architecture/reviewing-workflows.md",
            ]);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Equal("src/MeisterProPR.Application/MeisterProPR.Application.csproj", result.OwningProjectOrModule);
        Assert.Contains("tests/MeisterProPR.Application.Tests/Features/Reviewing/FooTests.cs", result.NearbyTests);
        Assert.Contains("src/MeisterProPR.Application/appsettings.json", result.ConfigTouchpoints);
        Assert.Contains("src/MeisterProPR.Application/DependencyInjection/ReviewingServiceCollectionExtensions.cs", result.RegistrationLocations);
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
