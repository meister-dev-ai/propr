// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Offline;

public sealed class FixtureReviewContextToolsTests
{
    [Fact]
    public async Task FixtureReviewContextTools_ExposeChangedFilesTreeAndFileContentFromFixture()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MEISTER_JWT_SECRET"] = "test-review-eval-jwt-secret-32!",
                })
            .Build();

        services.AddLogging();
        services.AddReviewEvalHarness(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var fixture = CreateFixture();
        var accessor = scope.ServiceProvider.GetRequiredService<IReviewEvaluationFixtureAccessor>();
        accessor.Fixture = fixture;

        var toolsFactory = scope.ServiceProvider.GetRequiredService<IReviewContextToolsFactory>();
        var tools = toolsFactory.Create(
            new ReviewContextToolsRequest(
                fixture.PullRequestSnapshot.CodeReview,
                fixture.PullRequestSnapshot.SourceBranch,
                1,
                null));

        var changedFiles = await tools.GetChangedFilesAsync(CancellationToken.None);
        var fileTree = await tools.GetFileTreeAsync("feature/offline-review", CancellationToken.None);
        var fileContent = await tools.GetFileContentAsync(
            "src/Example.cs",
            "feature/offline-review",
            1,
            4,
            CancellationToken.None);

        var changedFile = Assert.Single(changedFiles);
        Assert.Equal("src/Example.cs", changedFile.Path);
        Assert.Equal(ChangeType.Add, changedFile.ChangeType);
        Assert.Contains(".meister-propr/exclude", fileTree);
        Assert.Contains(".meister-propr/instructions-csharp.md", fileTree);
        Assert.Contains("public class Example", fileContent);
        Assert.Contains("public string Greet", fileContent);
    }

    private static ReviewEvaluationFixture CreateFixture()
    {
        return new ReviewEvaluationFixture(
            "fixture-sample",
            "1.0",
            new FixtureProvenance("synthetic"),
            new RepositorySnapshot(
                "feature/offline-review",
                "main",
                [
                    new RepositoryFileEntry(
                        "src/Example.cs",
                        "public class Example\n{\n    public string Greet(string name)\n    {\n        return \"Hello, \" + name + \"!\";\n    }\n}"),
                    new RepositoryFileEntry(
                        ".meister-propr/instructions-csharp.md",
                        "\"\"\"\ndescription: Review C# changes\nwhen-to-use: Apply when C# files are changed\n\"\"\"\nPrefer explicit string formatting when a change introduces user-facing text."),
                    new RepositoryFileEntry(
                        ".meister-propr/exclude",
                        "# Ignore generated files\ngenerated/**"),
                ],
                "sample-repository"),
            new PullRequestSnapshot(
                new CodeReviewRef(
                    new RepositoryRef(
                        new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/example"),
                        "sample-repository",
                        "sample-project",
                        "sample-project"),
                    CodeReviewPlatformKind.PullRequest,
                    "42",
                    42),
                new ReviewRevision("head-sha", "base-sha", null, null, null),
                "Sample review",
                "Offline review fixture",
                "feature/offline-review",
                "main",
                [
                    new FixtureChangedFile(
                        "src/Example.cs",
                        ChangeType.Add,
                        "+++ b/src/Example.cs\n@@ -0,0 +1,7 @@\n+public class Example\n+{\n+    public string Greet(string name)\n+    {\n+        return \"Hello, \" + name + \"!\";\n+    }\n+}",
                        "public class Example\n{\n    public string Greet(string name)\n    {\n        return \"Hello, \" + name + \"!\";\n    }\n}")]));
    }
}
