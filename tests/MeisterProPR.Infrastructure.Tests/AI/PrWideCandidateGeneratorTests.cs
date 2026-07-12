// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests the generate-only PR-wide entry point: it runs the plan -> investigate -> synthesize stages once on the
///     supplied pass runtime, tags the resulting candidates with PR-wide provenance ("Pass N" via the multi-pass-union
///     branch plus the pr_wide marker), and honors the generation budget. No model calls: the fake chat client drives
///     the deterministic fallback stages.
/// </summary>
public sealed class PrWideCandidateGeneratorTests
{
    [Fact]
    public async Task GenerateCandidatesAsync_RunsStagesOnceAndTagsPrWideProvenance()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var prWideProtocolId = Guid.NewGuid();
        recorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewPassKind?>(),
                Arg.Any<string?>())
            .Returns(prWideProtocolId);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ProtocolRecorder = recorder,
        };

        var sut = CreateSut();

        var candidates = await sut.GenerateCandidatesAsync(
            CreateJob(),
            CreatePr(),
            context,
            Runtime(Substitute.For<IChatClient>()),
            new PrWideGenerationBudget(3, 3, 5),
            unionPassIndex: 2,
            shadow: false,
            CancellationToken.None);

        Assert.NotEmpty(candidates);
        Assert.All(
            candidates, candidate =>
            {
                Assert.Equal(CandidateFindingProvenance.PrWidePassOrigin, candidate.Provenance.OriginKind);
                Assert.Equal(nameof(ReviewPassKind.MultiPassUnion), candidate.Provenance.ResolveOriginPassKindName());
                Assert.Equal(2, candidate.Provenance.UnionPassIndex);
                Assert.Equal(ReviewPassScope.PrWide, candidate.Provenance.UnionLens);
                Assert.StartsWith("finding-prw-", candidate.FindingId, StringComparison.Ordinal);
            });

        await recorder.Received(1).BeginAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            "pr-wide-review",
            null,
            AiConnectionModelCategory.HighEffort,
            "prwide-model",
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPassKind?>(),
            Arg.Any<string?>());
        await recorder.Received(1).SetCompletedAsync(
            prWideProtocolId,
            "Completed",
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateCandidatesAsync_CapsInvestigationsToBudget()
    {
        // The deterministic fallback plan proposes two investigations for this change set; a budget of one must trim
        // the plan so only a single investigation is launched.
        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewPassKind?>(),
                Arg.Any<string?>())
            .Returns(Guid.NewGuid());

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = recorder,
        };

        var sut = CreateSut();

        await sut.GenerateCandidatesAsync(
            CreateJob(),
            CreatePr(),
            context,
            Runtime(Substitute.For<IChatClient>()),
            new PrWideGenerationBudget(MaxInvestigations: 1, MaxToolCallsPerInvestigation: 3, MaxSeedFilesPerInvestigation: 5),
            unionPassIndex: 2,
            shadow: false,
            CancellationToken.None);

        await recorder.Received(1).RecordPrWideStageEventAsync(
            Arg.Any<Guid>(),
            ReviewProtocolEventNames.PrWideInvestigationLaunched,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateCandidatesAsync_WhenShadow_RecordsPassShadowCompletedAndMarksProvenance()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var prWideProtocolId = Guid.NewGuid();
        recorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewPassKind?>(),
                Arg.Any<string?>())
            .Returns(prWideProtocolId);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ProtocolRecorder = recorder,
        };

        var candidates = await CreateSut().GenerateCandidatesAsync(
            CreateJob(),
            CreatePr(),
            context,
            Runtime(Substitute.For<IChatClient>()),
            new PrWideGenerationBudget(3, 3, 5),
            unionPassIndex: 2,
            shadow: true,
            CancellationToken.None);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.True(candidate.Provenance.Shadow));

        await recorder.Received(1).RecordReviewStrategyEventAsync(
            prWideProtocolId,
            ReviewProtocolEventNames.PassShadowCompleted,
            Arg.Any<string?>(),
            Arg.Is<string?>(output => output != null && output.Contains("catchCount", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateCandidatesAsync_CandidateAnchoredOnChangedLine_ClassifiesScopeRelationOnChangedLine()
    {
        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");

        // No protocol recorder: the stage recorders and protocol begin are skipped, so the two chat responses map
        // cleanly to the planning call (no investigations) and the synthesis call.
        var context = new ReviewSystemContext(null, [], reviewTools);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Verify anchored cross-file finding."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR introduces a cross-file publication ordering risk.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "Publisher writes before the aggregator finishes.",
                                                  "severity": "warning",
                                                  "category": "cross_cutting",
                                                  "candidate_summary_text": "Potential cross-file ordering issue noted.",
                                                  "confidence": { "concern": "cross_file_reasoning", "score": 86 },
                                                  "supporting_files": ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"],
                                                  "file_path": "src/Core/Aggregator.cs",
                                                  "line_number": 2
                                                }
                                              ]
                                            }
                                            """)));

        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Refactor service registration",
            "Touches the aggregator and publisher.",
            "feature/registration",
            "main",
            [
                new ChangedFile(
                    "src/Core/Aggregator.cs", ChangeType.Edit, "aggregator content", "@@ -1,2 +1,2 @@\n+return staleAggregate;\n+publisher.Publish(result);"),
                new ChangedFile("src/Api/PublishController.cs", ChangeType.Edit, "controller content", "@@ -1,1 +1,1 @@\n+publisher.Publish(result);"),
            ]);

        var candidates = await CreateSut().GenerateCandidatesAsync(
            CreateJob(),
            pr,
            context,
            Runtime(chatClient),
            new PrWideGenerationBudget(3, 3, 5),
            unionPassIndex: 2,
            shadow: false,
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal("src/Core/Aggregator.cs", candidate.FilePath);
        Assert.Equal(2, candidate.LineNumber);
        Assert.Equal(ChangedLineRelation.OnChangedLine, candidate.ScopeRelation);
    }

    private static PrWideAgenticReviewOrchestrator CreateSut()
    {
        return new PrWideAgenticReviewOrchestrator(
            Substitute.For<IFileByFileReviewOrchestrator>(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>());
    }

    private static IResolvedAiChatRuntime Runtime(IChatClient client)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(client);
        runtime.Model.Returns(new AiConfiguredModelDto(Guid.NewGuid(), "prwide-model", "prwide-model", [AiOperationKind.Chat], [AiProtocolMode.Auto]));
        return runtime;
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
    }

    private static PullRequest CreatePr()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Refactor service registration",
            "Touches startup, composition root, and tests.",
            "feature/registration",
            "main",
            [
                new ChangedFile("src/Web/Program.cs", ChangeType.Edit, "program content", "+builder.Services.AddFoo();"),
                new ChangedFile("src/Application/Registration.cs", ChangeType.Edit, "registration content", "+services.AddFoo();"),
                new ChangedFile("tests/RegistrationTests.cs", ChangeType.Edit, "test content", "+Assert.NotNull(foo);"),
            ]);
    }
}
