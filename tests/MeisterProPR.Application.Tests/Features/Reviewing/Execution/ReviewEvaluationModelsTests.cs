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
            new EvaluationModelSelection(["gpt-4o"]),
            new EvaluationOutputOptions("artifacts/run.json", "full"),
            EnableProRV: false);

        var request = new ReviewWorkflowRequest(job, chatClient, "gpt-4o", fixture, configuration);

        Assert.Same(job, request.Job);
        Assert.Same(chatClient, request.ChatClient);
        Assert.Equal("gpt-4o", request.ModelId);
        Assert.Same(fixture, request.Fixture);
        Assert.Same(configuration, request.Configuration);
        Assert.NotNull(request.Configuration);
        Assert.False(request.Configuration.EnableProRV);
        Assert.Equal(ReviewAugmentationMode.Disabled, request.Configuration.EffectiveAugmentationMode);
        Assert.Equal(ReviewAugmentationMode.Disabled, request.EffectiveAugmentationMode);
    }

    [Fact]
    public void ReviewWorkflowRequest_ExplicitAugmentationModeOverridesConfiguration()
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
        var configuration = new EvaluationConfiguration(
            "baseline",
            new EvaluationModelSelection(["gpt-4o"]),
            new EvaluationOutputOptions("artifacts/run.json", "full"),
            AugmentationMode: ReviewAugmentationMode.Disabled);

        var request = new ReviewWorkflowRequest(
            job,
            chatClient,
            "gpt-4o",
            Configuration: configuration,
            AugmentationMode: ReviewAugmentationMode.LateAugmentation);

        Assert.Equal(ReviewAugmentationMode.LateAugmentation, request.EffectiveAugmentationMode);
    }

    [Fact]
    public void ReviewWorkflowRequest_OmittedAugmentationDefaultsToDisabled()
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

        var request = new ReviewWorkflowRequest(job, chatClient, "gpt-4o");

        Assert.Equal(ReviewAugmentationMode.Disabled, request.EffectiveAugmentationMode);
    }

    [Fact]
    public void EvaluationConfiguration_OmittedProRvDefaultsToDisabled()
    {
        var configuration = new EvaluationConfiguration(
            "baseline",
            new EvaluationModelSelection(["gpt-4o"]),
            new EvaluationOutputOptions("artifacts/run.json", "full"));

        Assert.False(configuration.EnableProRV);
        Assert.Equal(ReviewAugmentationMode.Disabled, configuration.EffectiveAugmentationMode);
    }

    [Fact]
    public void EvaluationConfiguration_ExplicitAugmentationModeOverridesLegacyBoolean()
    {
        var configuration = new EvaluationConfiguration(
            "late-steering",
            new EvaluationModelSelection(["gpt-4o"]),
            new EvaluationOutputOptions("artifacts/run.json", "full"),
            EnableProRV: true,
            AugmentationMode: ReviewAugmentationMode.LateAugmentation);

        Assert.Equal(ReviewAugmentationMode.LateAugmentation, configuration.EffectiveAugmentationMode);
    }

    [Fact]
    public void CandidateReviewFinding_WithMergedProvenanceCapturesLateSteeringMetadata()
    {
        var finding = new CandidateReviewFinding(
            "finding-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                "src/Example.cs",
                reviewPassKind: ReviewPassKind.Baseline,
                findingProvenanceKind: FindingProvenanceKind.BaselineOnly),
            CommentSeverity.Warning,
            "Baseline finding",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Example.cs",
            12);

        var merged = finding.WithMergedProvenance(
            FindingProvenanceKind.Both,
            [ReviewPassKind.Baseline, ReviewPassKind.ProRVAugmentation],
            "deduped identical semantic issue",
            "identity-1",
            "merge-group-1");

        Assert.NotNull(merged.MergedFinding);
        Assert.Equal(FindingProvenanceKind.Both, merged.MergedFinding!.Provenance);
        Assert.Equal([ReviewPassKind.Baseline, ReviewPassKind.ProRVAugmentation], merged.MergedFinding.SourcePasses);
        Assert.Equal("identity-1", merged.MergedFinding.IdentityKey);
        Assert.Equal("merge-group-1", merged.MergedFinding.MergeGroupKey);
    }

    [Fact]
    public void ReviewWorkflowResult_ExposesMergedCandidateFindingsAndAugmentationMode()
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/example",
            "sample-project",
            "sample-repository",
            42,
            1);
        var finding = new CandidateReviewFinding(
            "finding-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                "src/Example.cs"),
            CommentSeverity.Warning,
            "Merged finding",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Example.cs",
            7);
        var mergedFinding = new MergedCandidateFinding(
            finding,
            FindingProvenanceKind.BaselineOnly,
            [ReviewPassKind.Baseline],
            "identity-001",
            "baseline retained");

        var result = new ReviewWorkflowResult(
            job,
            new ReviewResult("summary", []),
            [],
            AugmentationMode: ReviewAugmentationMode.LateAugmentation,
            MergedCandidateFindings: [mergedFinding]);

        Assert.Equal(ReviewAugmentationMode.LateAugmentation, result.AugmentationMode);
        Assert.Single(result.MergedCandidateFindingsOrEmpty);
        Assert.Equal("identity-001", result.MergedCandidateFindingsOrEmpty[0].IdentityKey);
    }

    [Fact]
    public void ReviewEvaluationFixture_CanRepresentSyntheticRepositoryAndThreadContext()
    {
        var fixture = CreateFixture();

        Assert.Equal("fixture-sample", fixture.FixtureId);
        Assert.Equal("synthetic", fixture.Provenance.SourceKind);
        Assert.Single(fixture.RepositorySnapshot.Files);
        Assert.Single(fixture.PullRequestSnapshot.ChangedFiles);
        Assert.NotNull(fixture.Threads);
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
            new EvaluationConfigurationMetadata(
                "baseline",
                "gpt-4o",
                "full",
                ReviewStrategy.PrWideAgentic,
                true,
                ReviewAugmentationMode.EarlySteering.ToString(),
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["baselineOnly"] = 0,
                    ["proRvOnly"] = 0,
                    ["both"] = 0,
                },
                "baseline",
                [],
                false),
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
        Assert.Equal(ReviewStrategy.PrWideAgentic, artifact.Configuration.Strategy);
        Assert.True(artifact.Configuration.EnableProRV);
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
            ],
            ProRVPrefilterExpectations: new FixtureProRVPrefilterExpectations([]));
    }
}
