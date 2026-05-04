// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ReviewEvaluationModelsTests
{
    [Fact]
    public void ReviewWorkflowRequest_CapturesOfflineExecutionInputs()
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/example",
            "sample-project",
            "sample-repository",
            42,
            1);
        var chatClient = Substitute.For<IChatClient>();
        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "baseline",
            ModelSelection: new EvaluationModelSelection(["gpt-4o"]),
            Output: new EvaluationOutputOptions("artifacts/run.json", "full"));

        var request = new ReviewWorkflowRequest(job, chatClient, "gpt-4o", fixture, configuration);

        Assert.Same(job, request.Job);
        Assert.Same(chatClient, request.ChatClient);
        Assert.Equal("gpt-4o", request.ModelId);
        Assert.Same(fixture, request.Fixture);
        Assert.Same(configuration, request.Configuration);
    }

    [Fact]
    public void ReviewEvaluationFixture_CanRepresentSyntheticRepositoryAndThreadContext()
    {
        var fixture = CreateFixture();

        Assert.Equal("fixture-sample", fixture.FixtureId);
        Assert.Equal("synthetic", fixture.Provenance.SourceKind);
        Assert.Single(fixture.RepositorySnapshot.Files);
        Assert.Single(fixture.PullRequestSnapshot.ChangedFiles);
        Assert.Single(fixture.Threads);
    }

    [Fact]
    public void EvaluationArtifact_CapturesPortableMeasurementSurfaces()
    {
        var artifact = new EvaluationArtifact(
            new EvaluationRunMetadata(
                "run-1",
                DateTimeOffset.Parse("2026-04-29T09:00:00Z"),
                DateTimeOffset.Parse("2026-04-29T09:01:00Z"),
                "completed",
                "resolved"),
            new EvaluationFixtureMetadata("fixture-sample", "1.0", "synthetic"),
            new EvaluationConfigurationMetadata("baseline", "gpt-4o", "full"),
            new ReviewResult(
                "Sample summary",
                [
                    new ReviewComment("src/Example.cs", 1, CommentSeverity.Warning, "Sample warning"),
                ])
            {
                CarriedForwardFilePaths = ["src/Shared.cs"],
                CarriedForwardCandidatesSkipped = 1,
            },
            [
                new StageEvidenceRecord(
                    "synthesis",
                    "Synthesis",
                    null,
                    "completed",
                    1,
                    0,
                    20,
                    10,
                    null,
                    "gpt-4o",
                    AiConnectionModelCategory.HighEffort,
                    [
                        new StageEvidenceEvent(
                            ProtocolEventKind.AiCall,
                            "ai_call_iter_1",
                            DateTimeOffset.Parse("2026-04-29T09:00:30Z"),
                            20,
                            10,
                            "summarize the review",
                            "You are an expert reviewer.",
                            "Sample summary",
                            null),
                    ]),
            ],
            new EvaluationTokenUsage(
                20,
                10,
                [new EvaluationTokenUsageBreakdown("gpt-4o", 20, 10)],
                [new EvaluationTokenUsageBreakdown("highEffort", 20, 10)]),
            ["thread memory was unavailable"]);

        Assert.Equal("completed", artifact.Run.Outcome);
        Assert.Equal("fixture-sample", artifact.Fixture.FixtureId);
        Assert.Equal("baseline", artifact.Configuration.ConfigurationId);
        Assert.Single(artifact.FinalResult.Comments);
        Assert.Single(artifact.Stages);
        Assert.Equal(20, artifact.TokenUsage.TotalInputTokens);
        Assert.Single(artifact.Warnings);
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
                    new RepositoryFileEntry("src/Example.cs", "public class Example {}"),
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
                        "+++ b/src/Example.cs\n@@ -0,0 +1 @@\n+public class Example {}",
                        "public class Example {}"),
                ]),
            [
                new FixtureThread(
                    1001,
                    "src/Example.cs",
                    1,
                    "Active",
                    [
                        new FixtureThreadComment("Reviewer", "Please confirm this change."),
                    ]),
            ]);
    }
}
