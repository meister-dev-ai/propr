// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class PromptExperimentBatchRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesAllVariantRunsAndWritesArtifactsWithVariantMetadata()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
        var protectedValueResolver = Substitute.For<IProtectedValueResolver>();

        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "config-a",
            new EvaluationModelSelection(["gpt-5.4"]),
            new EvaluationOutputOptions("artifacts/original.json", "full"),
            AiConnection: new EvaluationAiConnection("https://ai.example", "api-key", EvaluationAiConnection.AzureOpenAiKey),
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
                    },
                    [FileByFileReviewStepIds.PrVerification]),
            ]);

        var chatClient = Substitute.For<IChatClient>();
        GivenRuntimeFor(runtimeFactory, chatClient);
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
                    ]);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            runtimeFactory,
            protectedValueResolver);

        var result = await sut.RunAsync(batch, fixture, configuration, CreateJobTemplate(fixture), CancellationToken.None);

        await validator.Received(1).ValidateAsync(batch, CancellationToken.None);
        await workflowRunner.Received(2).RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>());
        await workflowRunner.Received(1).RunAsync(
            Arg.Is<ReviewWorkflowRequest>(request => request.EffectiveSkippedSteps.Contains(FileByFileReviewStepIds.PrVerification)),
            Arg.Any<CancellationToken>());
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

    // The harness names its endpoint in a configuration file, so the profile the shared construction step receives
    // is assembled from that file. Going through that step is what puts a harness run on the same driver, retry
    // classification and request shaping a review gets.
    [Fact]
    public async Task RunAsync_BuildsTheRunClientFromTheConfiguredConnectionThroughTheSharedConstructionStep()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
        var protectedValueResolver = Substitute.For<IProtectedValueResolver>();

        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "config-a",
            new EvaluationModelSelection(["claude-sonnet-4-5"]),
            new EvaluationOutputOptions("artifacts/original.json", "full"),
            AiConnection: new EvaluationAiConnection("https://anthropic.example", "api-key", "meisterdev/anthropic"),
            ProtectedValueReferences: [new ProtectedValueReference("api-key", "Meister:ApiKey")]);
        var batch = new PromptExperimentBatch(
            "batch-001",
            fixture.FixtureId,
            fixture.ActiveScenarioIdOrNull,
            configuration.ConfigurationId,
            [new PromptExperimentRunRequest("run-baseline", "baseline", "artifacts/baseline.json")]);

        var chatClient = Substitute.For<IChatClient>();
        GivenRuntimeFor(runtimeFactory, chatClient);
        protectedValueResolver.ResolveAsync(configuration.ProtectedValueReferencesOrEmpty, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "secret-value" });

        workflowRunner.RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ReviewWorkflowRequest>();
                return new ReviewWorkflowResult(request.Job, new ReviewResult("summary", []), []);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            runtimeFactory,
            protectedValueResolver);

        await sut.RunAsync(batch, fixture, configuration, CreateJobTemplate(fixture), CancellationToken.None);

        runtimeFactory.Received(1).CreateChatRuntime(
            Arg.Is<AiConnectionDto>(connection =>
                connection.ProviderKind == "meisterdev/anthropic"
                && connection.BaseUrl == "https://anthropic.example"
                && connection.AuthMode == "meisterdev/anthropic:ApiKey"
                && connection.Secret == "secret-value"),
            Arg.Is<AiConfiguredModelDto>(model => model.RemoteModelId == "claude-sonnet-4-5" && model.SupportsChat),
            Arg.Any<AiPurposeBindingDto>(),
            Arg.Any<string?>());

        // The run's every workflow call goes out on the client that step produced.
        await workflowRunner.Received(1).RunAsync(
            Arg.Is<ReviewWorkflowRequest>(request => ReferenceEquals(request.ChatClient, chatClient)),
            Arg.Any<CancellationToken>());
    }

    // An Azure resource reached without a key authenticates from the ambient credential chain.
    [Fact]
    public async Task RunAsync_AzureConnectionWithNoResolvedKey_BuildsTheProfileForAmbientAzureIdentity()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
        var protectedValueResolver = Substitute.For<IProtectedValueResolver>();

        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "config-a",
            new EvaluationModelSelection(["gpt-5.4"]),
            new EvaluationOutputOptions("artifacts/original.json", "full"),
            AiConnection: new EvaluationAiConnection("https://contoso.openai.azure.com/", null, EvaluationAiConnection.AzureOpenAiKey));
        var batch = new PromptExperimentBatch(
            "batch-001",
            fixture.FixtureId,
            fixture.ActiveScenarioIdOrNull,
            configuration.ConfigurationId,
            [new PromptExperimentRunRequest("run-baseline", "baseline", "artifacts/baseline.json")]);

        GivenRuntimeFor(runtimeFactory, Substitute.For<IChatClient>());
        workflowRunner.RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ReviewWorkflowRequest>();
                return new ReviewWorkflowResult(request.Job, new ReviewResult("summary", []), []);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            runtimeFactory,
            protectedValueResolver);

        await sut.RunAsync(batch, fixture, configuration, CreateJobTemplate(fixture), CancellationToken.None);

        runtimeFactory.Received(1).CreateChatRuntime(
            Arg.Is<AiConnectionDto>(connection =>
                connection.AuthMode == "meisterdev/azureOpenAi:AzureIdentity" && connection.Secret == null),
            Arg.Any<AiConfiguredModelDto>(),
            Arg.Any<AiPurposeBindingDto>(),
            Arg.Any<string?>());
    }

    // The runtime keys its per-provider throttle gate on the connection identifier, so two harness endpoints
    // carrying one identifier pace each other against a quota only one of them has. The identifier is derived
    // from the endpoint, so it also stays the same across runs against the same endpoint.
    [Fact]
    public async Task RunAsync_TwoEndpoints_BuildProfilesWithDistinctAndRepeatableIdentifiers()
    {
        var anthropic = new EvaluationAiConnection("https://anthropic.example", null, "meisterdev/anthropic");
        var openAi = new EvaluationAiConnection("https://api.openai.com/v1", null, "meisterdev/openAi");

        var first = await CapturedConnectionIdAsync(anthropic);
        var second = await CapturedConnectionIdAsync(openAi);
        var firstAgain = await CapturedConnectionIdAsync(anthropic);

        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(first, second);
        Assert.Equal(first, firstAgain);
    }

    private static async Task<Guid> CapturedConnectionIdAsync(EvaluationAiConnection aiConnection)
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
        var protectedValueResolver = Substitute.For<IProtectedValueResolver>();

        var fixture = CreateFixture();
        var configuration = new EvaluationConfiguration(
            "config-a",
            new EvaluationModelSelection(["some-model"]),
            new EvaluationOutputOptions("artifacts/original.json", "full"),
            AiConnection: aiConnection);
        var batch = new PromptExperimentBatch(
            "batch-001",
            fixture.FixtureId,
            fixture.ActiveScenarioIdOrNull,
            configuration.ConfigurationId,
            [new PromptExperimentRunRequest("run-baseline", "baseline", "artifacts/baseline.json")]);

        GivenRuntimeFor(runtimeFactory, Substitute.For<IChatClient>());
        workflowRunner.RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ReviewWorkflowRequest>();
                return new ReviewWorkflowResult(request.Job, new ReviewResult("summary", []), []);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            runtimeFactory,
            protectedValueResolver);

        await sut.RunAsync(batch, fixture, configuration, CreateJobTemplate(fixture), CancellationToken.None);

        return runtimeFactory.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAiRuntimeFactory.CreateChatRuntime))
            .Select(call => ((AiConnectionDto)call.GetArguments()[0]!).Id)
            .Single();
    }

    [Fact]
    public async Task RunAsync_UsesDefaultPromptEvidenceForUntargetedStage()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
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
                    [CreateProtocol(request.Job.Id, "synthesis", "ai_call_iter_1", "system-default", "user-default")]);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            runtimeFactory,
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
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
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
                    [CreateProtocolWithPromptEvidence(request.Job.Id, variantName, PromptStageKeys.PerFileUser, PromptCompositionMode.Replace)]);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            runtimeFactory,
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

    [Fact]
    public async Task RunAsync_PreservesProCursorScopeWhenCloningJobTemplate()
    {
        var workflowRunner = Substitute.For<IReviewWorkflowRunner>();
        var validator = Substitute.For<IReviewPromptExperimentValidator>();
        var artifactWriter = Substitute.For<IEvaluationArtifactWriter>();
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
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
        var selectedSourceId = Guid.NewGuid();
        var jobTemplate = CreateJobTemplate(fixture);
        jobTemplate.SetProCursorSourceScope(ProCursorSourceScopeMode.SelectedSources, [selectedSourceId]);

        workflowRunner.RunAsync(Arg.Any<ReviewWorkflowRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ReviewWorkflowRequest>();
                return new ReviewWorkflowResult(
                    request.Job,
                    new ReviewResult("summary", []),
                    [CreateProtocol(request.Job.Id, "synthesis", "ai_call_iter_1", "system-default", "user-default")]);
            });
        artifactWriter.WriteAsync(Arg.Any<EvaluationArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<string>(1)));

        var sut = new PromptExperimentBatchRunner(
            workflowRunner,
            validator,
            artifactWriter,
            runtimeFactory,
            protectedValueResolver);

        await sut.RunAsync(batch, fixture, configuration, jobTemplate, CancellationToken.None);

        await workflowRunner.Received(1).RunAsync(
            Arg.Is<ReviewWorkflowRequest>(request => HasSelectedKnowledgeSourceScope(request, selectedSourceId)),
            Arg.Any<CancellationToken>());
    }

    private static bool HasSelectedKnowledgeSourceScope(ReviewWorkflowRequest request, Guid sourceId)
    {
        return request.Job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
               && request.Job.ProCursorSourceIds.SequenceEqual(new[] { sourceId });
    }

    private static void GivenRuntimeFor(IAiRuntimeFactory runtimeFactory, IChatClient chatClient)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        runtimeFactory.CreateChatRuntime(
                Arg.Any<AiConnectionDto>(),
                Arg.Any<AiConfiguredModelDto>(),
                Arg.Any<AiPurposeBindingDto>(),
                Arg.Any<string?>())
            .Returns(runtime);
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
        job.SetReviewPipelineProfile(ReviewPipelineProfileCatalog.FileByFileBalancedProfileId);
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
                    "ai-call",
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
                    "ai-call",
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
                    "review-strategy",
                    null),
            ]);
    }
}
