// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

public sealed class PromptExperimentBatchRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesAllVariantRunsAndWritesArtifactsWithVariantMetadata()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var chatClientFactory = Substitute.For<IAiChatClientFactory>();
        var protectedValueResolver = Substitute.For<IProtectedValueResolver>();

        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "config-a",
            new EvaluationModelSelection(["gpt-5.4"]),
            new EvaluationOutputOptions("artifacts/original.json", "full"),
            AiConnection: new EvaluationAiConnection("https://ai.example", "api-key"),
            ProtectedValueReferences: [new ProtectedValueReference("api-key", "Meister:ApiKey")]);
        var batch = new PromptExperimentBatch(
            "batch-001",
            fixture.FixtureId,
            fixture.ActiveScenarioIdOrNull,
            configuration.ConfigurationId,
            [
                new PromptExperimentRunRequest(
                    "run-baseline",
                    "baseline",
                    "artifacts/baseline.json"),
                new PromptExperimentRunRequest(
                    "run-variant",
                    "per-file-shorter-user",
                    "artifacts/per-file-shorter-user.json",
                    [new StagePromptVariant(PromptStageKeys.PerFileUser, PromptStageRole.User, PromptCompositionMode.Replace, "shorter user prompt")],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["comparisonGroup"] = "fixture-001",
                    }),
            ]);

        var chatClient = Substitute.For<IChatClient>();
        chatClientFactory.CreateClient("https://ai.example", "secret-value").Returns(chatClient);
        protectedValueResolver.ResolveAsync(configuration.ProtectedValueReferencesOrEmpty, Arg.Any<CancellationToken>())
            .Returns(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["api-key"] = "secret-value",
                });

        workflowRunner.RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ReviewWorkflowRequest>();
                var variantName = request.PromptExperiment?.VariantName ?? "baseline";
                return new ReviewWorkflowResult(
                    request.Job,
                    new ReviewResult($"summary-{variantName}", []),
                    [
                        CreateProtocol(
                            request.Job.Id,
                            "src/Foo.cs",
                            "ai_call_iter_1",
                            $"system-{variantName}",
                            $"user-{variantName}"),
                    ],
                    AugmentationMode: request.EffectiveAugmentationMode);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            chatClientFactory,
            protectedValueResolver);

        var result = await sut.RunAsync(batch, fixture, configuration, CreateJobTemplate(fixture), CancellationToken.None);

        await validator.Received(1).ValidateAsync(batch, CancellationToken.None);
        await workflowRunner.Received(2).RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>());
        await artifactWriter.Received(1).WriteAsync(
            Arg.Is<EvaluationArtifact>(artifact =>
                artifact.Run.RunId == "run-baseline" &&
                artifact.Configuration.VariantName == "baseline" &&
                !artifact.Configuration.UsedPromptExperiment &&
                artifact.Configuration.TargetedStageKeys.Count == 0),
            "artifacts/baseline.json",
            Arg.Any<CancellationToken>());
        await artifactWriter.Received(1).WriteAsync(
            Arg.Is<EvaluationArtifact>(artifact => VariantArtifactMatches(artifact)),
            "artifacts/per-file-shorter-user.json",
            Arg.Any<CancellationToken>());
        Assert.Equal(["artifacts/baseline.json", "artifacts/per-file-shorter-user.json"], result.ArtifactPaths);
    }

    [Fact]
    public async Task RunAsync_UsesDefaultPromptEvidenceForUntargetedStage()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var chatClientFactory = Substitute.For<IAiChatClientFactory>();
        var protectedValueResolver = Substitute.For<IProtectedValueResolver>();

        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "config-a",
            new EvaluationModelSelection(["gpt-5.4"]),
            new EvaluationOutputOptions("artifacts/original.json", "full"));
        var batch = new PromptExperimentBatch(
            "batch-001",
            fixture.FixtureId,
            fixture.ActiveScenarioIdOrNull,
            configuration.ConfigurationId,
            [new PromptExperimentRunRequest("run-baseline", "baseline", "artifacts/baseline.json")]);

        workflowRunner.RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ReviewWorkflowRequest>();
                return new ReviewWorkflowResult(
                    request.Job,
                    new ReviewResult("summary", []),
                    [CreateProtocol(request.Job.Id, "synthesis", "ai_call_iter_1", "system-default", "user-default")],
                    AugmentationMode: request.EffectiveAugmentationMode);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            chatClientFactory,
            protectedValueResolver);

        await sut.RunAsync(batch, fixture, configuration, CreateJobTemplate(fixture), CancellationToken.None);

        await artifactWriter.Received(1).WriteAsync(
            Arg.Is<EvaluationArtifact>(artifact => BaselineArtifactMatches(artifact)),
            "artifacts/baseline.json",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RepeatedTargetedVariants_PreservesPerVariantPromptEvidenceForSameStage()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var chatClientFactory = Substitute.For<IAiChatClientFactory>();
        var protectedValueResolver = Substitute.For<IProtectedValueResolver>();

        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "config-a",
            new EvaluationModelSelection(["gpt-5.4"]),
            new EvaluationOutputOptions("artifacts/original.json", "full"));
        var batch = new PromptExperimentBatch(
            "batch-001",
            fixture.FixtureId,
            fixture.ActiveScenarioIdOrNull,
            configuration.ConfigurationId,
            [
                new PromptExperimentRunRequest(
                    "run-variant-a",
                    "variant-a",
                    "artifacts/variant-a.json",
                    [new StagePromptVariant(PromptStageKeys.PerFileUser, PromptStageRole.User, PromptCompositionMode.Replace, "prompt A")]),
                new PromptExperimentRunRequest(
                    "run-variant-b",
                    "variant-b",
                    "artifacts/variant-b.json",
                    [new StagePromptVariant(PromptStageKeys.PerFileUser, PromptStageRole.User, PromptCompositionMode.Replace, "prompt B")]),
            ]);

        workflowRunner.RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ReviewWorkflowRequest>();
                var variantName = request.PromptExperiment!.VariantName;
                return new ReviewWorkflowResult(
                    request.Job,
                    new ReviewResult($"summary-{variantName}", []),
                    [CreateProtocolWithPromptEvidence(request.Job.Id, variantName, PromptStageKeys.PerFileUser, PromptCompositionMode.Replace)],
                    AugmentationMode: request.EffectiveAugmentationMode);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            chatClientFactory,
            protectedValueResolver);

        await sut.RunAsync(batch, fixture, configuration, CreateJobTemplate(fixture), CancellationToken.None);

        await artifactWriter.Received(1).WriteAsync(
            Arg.Is<EvaluationArtifact>(artifact => RepeatedVariantArtifactMatches(artifact, "variant-a", "user-variant-a")),
            "artifacts/variant-a.json",
            Arg.Any<CancellationToken>());
        await artifactWriter.Received(1).WriteAsync(
            Arg.Is<EvaluationArtifact>(artifact => RepeatedVariantArtifactMatches(artifact, "variant-b", "user-variant-b")),
            "artifacts/variant-b.json",
            Arg.Any<CancellationToken>());
    }

    private static bool VariantArtifactMatches(EvaluationArtifact artifact)
    {
        var stage = Assert.Single(artifact.Stages);
        var evidence = Assert.IsType<PromptExperimentEvidence>(stage.PromptExperimentEvidence);

        return artifact.Run.RunId == "run-variant"
               && artifact.Configuration.VariantName == "per-file-shorter-user"
               && artifact.Configuration.UsedPromptExperiment
               && artifact.Configuration.TargetedStageKeys.SequenceEqual(new[] { PromptStageKeys.PerFileUser }, StringComparer.Ordinal)
               && evidence.UserPromptText == "user-per-file-shorter-user";
    }

    private static bool BaselineArtifactMatches(EvaluationArtifact artifact)
    {
        var stage = Assert.Single(artifact.Stages);
        var evidence = Assert.IsType<PromptExperimentEvidence>(stage.PromptExperimentEvidence);

        return evidence.StageKey == PromptStageKeys.SynthesisSystem
               && evidence.UsedDefaultConstruction
               && evidence.CompositionMode == PromptCompositionMode.Default
               && evidence.SystemPromptText == "system-default"
               && evidence.UserPromptText == "user-default";
    }

    private static bool RepeatedVariantArtifactMatches(EvaluationArtifact artifact, string variantName, string expectedUserPrompt)
    {
        var stage = Assert.Single(artifact.Stages);
        var evidence = Assert.IsType<PromptExperimentEvidence>(stage.PromptExperimentEvidence);

        return artifact.Configuration.VariantName == variantName
               && artifact.Configuration.TargetedStageKeys.SequenceEqual([PromptStageKeys.PerFileUser], StringComparer.Ordinal)
               && evidence.StageKey == PromptStageKeys.PerFileUser
               && evidence.VariantName == variantName
               && evidence.UserPromptText == expectedUserPrompt;
    }

    private static ReviewJob CreateJobTemplate(ReviewEvaluationFixture fixture)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.JobOverride,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);
        return job;
    }

    private static ReviewEvaluationFixture CreateFixture()
    {
        return new ReviewEvaluationFixture(
            "fixture-001",
            "1.0",
            new FixtureProvenance("synthetic"),
            new RepositorySnapshot(
                "feature/offline-review",
                "main",
                [new RepositoryFileEntry("src/Foo.cs", "public class Foo {}")],
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
                        "src/Foo.cs",
                        ChangeType.Edit,
                        "@@ -1 +1 @@\n-public class Foo {}\n+public class Foo { }",
                        "public class Foo { }"),
                ]));
    }

    private static ReviewJobProtocolDto CreateProtocol(
        Guid jobId,
        string label,
        string eventName,
        string? systemPrompt,
        string? userPrompt)
    {
        return new ReviewJobProtocolDto(
            Guid.NewGuid(),
            jobId,
            1,
            label,
            null,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            "Completed",
            12,
            6,
            1,
            0,
            null,
            AiConnectionModelCategory.Default,
            "gpt-5.4",
            "summary",
            [],
            [
                new ProtocolEventDto(
                    Guid.NewGuid(),
                    ProtocolEventKind.AiCall,
                    eventName,
                    DateTimeOffset.UtcNow,
                    12,
                    6,
                    userPrompt,
                    systemPrompt,
                    "summary",
                    null),
            ]);
    }

    private static ReviewJobProtocolDto CreateProtocolWithPromptEvidence(
        Guid jobId,
        string variantName,
        string stageKey,
        PromptCompositionMode compositionMode)
    {
        return new ReviewJobProtocolDto(
            Guid.NewGuid(),
            jobId,
            1,
            "src/Foo.cs",
            null,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            "Completed",
            12,
            6,
            1,
            0,
            null,
            AiConnectionModelCategory.Default,
            "gpt-5.4",
            "summary",
            [],
            [
                new ProtocolEventDto(
                    Guid.NewGuid(),
                    ProtocolEventKind.AiCall,
                    "ai_call_iter_1",
                    DateTimeOffset.UtcNow,
                    12,
                    6,
                    $"user-{variantName}",
                    $"system-{variantName}",
                    "summary",
                    null),
                new ProtocolEventDto(
                    Guid.NewGuid(),
                    ProtocolEventKind.Operational,
                    ReviewProtocolEventNames.PromptStageEvidenceRecorded,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    $"user-{variantName}",
                    $"system-{variantName}",
                    $"{{\"stageKey\":\"{stageKey}\",\"variantName\":\"{variantName}\",\"compositionMode\":\"{compositionMode.ToString().ToLowerInvariant()}\",\"usedDefaultConstruction\":false}}",
                    null),
            ]);
    }
}
