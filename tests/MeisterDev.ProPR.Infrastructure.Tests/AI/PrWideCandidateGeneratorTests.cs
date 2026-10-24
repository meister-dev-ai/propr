// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests the generate-only PR-wide entry point: it runs the plan -> investigate -> synthesize stages once on the
///     supplied pass runtime, tags the resulting candidates with PR-wide provenance ("Pass N" via the multi-pass-union
///     branch plus the pr_wide marker), and honors the generation budget. No model calls: the fake chat client drives
///     the deterministic fallback stages.
/// </summary>
public sealed class PrWideCandidateGeneratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentInvestigationsWithTheSameLocalCandidateIdCannotTransferEachOthersTypes(bool repeatedTaskId)
    {
        var firstCustomId = Guid.NewGuid();
        var secondCustomId = Guid.NewGuid();
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("source");
        var recorder = Substitute.For<IProtocolRecorder>();
        var diagnostics = new List<string>();
        recorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(), ReviewProtocolEventNames.PrWideInvestigationResult, Arg.Any<string?>(),
                Arg.Do<string?>(output => diagnostics.Add(output ?? string.Empty)), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new ReviewSystemContext(null, [], tools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = recorder,
            FindingTypeVocabulary = new(
                1,
                [
                    new(firstCustomId, "tenant-boundary", "Tenant boundary", "Cross-tenant access."),
                    new(secondCustomId, "write-ordering", "Write ordering", "Concurrent writes."),
                ]),
        };
        var plan = JsonSerializer.Serialize(
            new
            {
                plan_id = "plan", concerns = new[] { "Isolation", "Write ordering" }, changed_areas = new[] { "src" },
                investigation_tasks = new[]
                {
                    new
                    {
                        id = "task", task_type = "concern", concern = "Isolation", seed_file_paths = new[] { "src/Web/Program.cs" },
                        allowed_tools = new[] { "get_file_content" }, max_tool_calls = 1
                    },
                    new
                    {
                        id = repeatedTaskId ? "task" : "other-task", task_type = "concern", concern = "Write ordering",
                        seed_file_paths = new[] { "src/Application/Registration.cs" }, allowed_tools = new[] { "get_file_content" }, max_tool_calls = 1
                    },
                },
            });
        var sourceIds = new List<string>();
        var callCount = 0;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                callCount++;
                string payload;
                if (callCount == 1)
                {
                    payload = plan;
                }
                else if (callCount <= 3)
                {
                    var first = callCount == 2;
                    payload = JsonSerializer.Serialize(
                        new
                        {
                            candidate_findings = new[]
                            {
                                new
                                {
                                    id = "candidate-001", message = first ? "First source claim." : "Second source claim.", category = "per_file_comment",
                                    severity = "error", file_path = first ? "src/Web/Program.cs" : "src/Application/Registration.cs", line_number = 1,
                                    finding_classification = new
                                        { types = first ? new[] { "logic-error", "tenant-boundary" } : ["concurrency", "write-ordering"] }
                                },
                            },
                        });
                }
                else
                {
                    var input = call.Arg<IEnumerable<ChatMessage>>().Last().Text;
                    foreach (var message in new[] { "First source claim.", "Second source claim." })
                    {
                        var line = input.Split('\n').Single(value =>
                            value.StartsWith("- ", StringComparison.Ordinal) && value.EndsWith(": " + message, StringComparison.Ordinal));
                        sourceIds.Add(line[2..line.IndexOf(':')]);
                    }

                    payload = JsonSerializer.Serialize(
                        new
                        {
                            summary = "Review.", candidate_findings = new[]
                            {
                                new
                                {
                                    id = "synthesized", message = "The first source defect, reworded.", category = "per_file_comment", severity = "error",
                                    file_path = "src/Web/Program.cs", line_number = 1, supporting_finding_ids = new[] { sourceIds[0] }
                                },
                            },
                        });
                }

                return new ChatResponse(new ChatMessage(ChatRole.Assistant, payload));
            });

        var candidates = await CreateSut().GenerateCandidatesAsync(
            CreateJob(), CreatePr(), context, Runtime(chat), new(2, 1, 1), 2, false, ReviewReasoningEffort.None, CancellationToken.None);

        var classification = Assert.Single(candidates).FindingClassification;
        Assert.NotNull(classification);
        Assert.Equal(["logic-error"], classification.CoreSlugs);
        Assert.Equal(firstCustomId, Assert.Single(classification.CustomTags).Id);
        Assert.Equal(2, sourceIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            using var parsed = JsonDocument.Parse(diagnostic);
            var mapping = Assert.Single(parsed.RootElement.GetProperty("candidateIdentityMappings").EnumerateArray());
            Assert.Equal("candidate-001", mapping.GetProperty("localId").GetString());
            Assert.Contains(mapping.GetProperty("id").GetString(), sourceIds);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unchanged")]
    [InlineData("combined")]
    [InlineData("combined-unchanged")]
    [InlineData("new")]
    public async Task GenerateCandidatesAsyncPreservesCapturedInvestigationTypesThroughSynthesis(string synthesisMode)
    {
        var combined = synthesisMode.StartsWith("combined", StringComparison.Ordinal);
        var customId = Guid.NewGuid();
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("return rows.Delete();");
        var context = new ReviewSystemContext(null, [], tools)
        {
            FindingTypeVocabulary = new(1, [new(customId, "tenant-boundary", "Tenant boundary", "Cross-tenant access.")]),
        };
        const string original = """
                                {"id":"first","message":"Deleting a tenant removes another tenant's rows.","category":"per_file_comment","severity":"error",
                                "file_path":"src/Web/Program.cs","line_number":1,"supporting_files":["src/Web/Program.cs"],
                                "finding_classification":{"types":["logic-error","tenant-boundary"],"level":"member","qualifier":"incorrect","confidence":0.96}}
                                """;
        const string second = """
                              {"id":"second","message":"Concurrent deletes lose a write.","category":"per_file_comment","severity":"error",
                              "file_path":"src/Application/Registration.cs","line_number":1,"supporting_files":["src/Application/Registration.cs"],
                              "finding_classification":{"types":["concurrency"],"level":"member","qualifier":"missing","confidence":0.9}}
                              """;
        var synthesized = JsonSerializer.Serialize(
            new
            {
                id = combined ? "combined" : "first",
                message = synthesisMode switch
                {
                    "combined" => "Deleting a tenant can lose other tenants' rows and concurrent writes.",
                    "new" => "A different validated security claim.",
                    _ => "Deleting a tenant removes another tenant's rows.",
                },
                category = "per_file_comment",
                severity = "error",
                file_path = "src/Web/Program.cs",
                line_number = 1,
                supporting_files = new[] { "src/Web/Program.cs" },
                supporting_finding_ids = combined ? new[] { "candidate-investigation-001-001", "candidate-investigation-001-002" } : [],
                finding_classification = synthesisMode == "missing"
                    ? null
                    : new
                    {
                        types = new[] { combined ? "logic-error" : "security" },
                        level = "member", qualifier = "incorrect", confidence = 0.98,
                    },
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {"plan_id":"plan","concerns":["Delete isolation"],"changed_areas":["src/Web"],
                                             "investigation_tasks":[{"id":"task","task_type":"concern","concern":"Delete isolation",
                                             "seed_file_paths":["src/Web/Program.cs"],"allowed_tools":["get_file_content"],"max_tool_calls":1}]}
                                            """)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"task_id\":\"task\",\"candidate_findings\":[" + original + "," + second + "]}")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"summary\":\"Review.\",\"candidate_findings\":[" + synthesized + "]}")));

        var candidates = await CreateSut().GenerateCandidatesAsync(
            CreateJob(), CreatePr(), context, Runtime(chat), new(1, 1, 1), 2, false, ReviewReasoningEffort.None, CancellationToken.None);

        var classification = Assert.Single(candidates).FindingClassification;
        Assert.NotNull(classification);
        if (synthesisMode == "new")
        {
            Assert.Equal(["security"], classification.CoreSlugs);
            Assert.Empty(classification.CustomTags);
        }
        else
        {
            Assert.Equal(combined ? ["logic-error", "concurrency"] : new[] { "logic-error" }, classification.CoreSlugs);
            Assert.Equal(customId, Assert.Single(classification.CustomTags).Id);
        }

        await chat.Received(3).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{\"candidate_findings\":[]}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"candidate_findings\":[{\"message\":\"incomplete candidate\"}]}", false)]
    public async Task PrWideProofDistinguishesCertifiedZeroFromFallbackArtifacts(string generation, bool complete)
    {
        var store = Substitute.For<IReviewFileResultStore>();
        var retained = new List<ModelReviewManifest>();
        store.RecordModelManifestAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Do<ModelReviewManifest>(retained.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "{\"plan_id\":\"plan\",\"concerns\":[],\"changed_areas\":[],\"investigation_tasks\":[],\"no_investigation_reason\":\"Direct synthesis\"}")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, generation)));
        var runtime = Runtime(chat);
        runtime.LogicalModelName.Returns("reviewer");
        var sut = new PrWideAgenticReviewOrchestrator(
            Substitute.For<IFileByFileReviewOrchestrator>(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()), Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            modelEvidenceStore: store);
        await sut.GenerateCandidatesAsync(
            CreateJob(), CreatePr(), new ReviewSystemContext(null, [], null), runtime,
            new PrWideGenerationBudget(1, 1, 1), 2, false, ReviewReasoningEffort.None, CancellationToken.None);

        Assert.NotEmpty(retained);
        var passes = retained[^1].PullRequestPasses.SelectMany(proof => proof.Passes).ToArray();
        Assert.NotEmpty(passes);
        Assert.All(
            passes, pass =>
            {
                Assert.Equal(complete, pass.IsCertified);
                Assert.Equal("reviewer", pass.LogicalModelName);
                Assert.Empty(pass.Candidates);
            });
    }

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
                Arg.Any<string?>(),
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
            reasoningEffort: ReviewReasoningEffort.None,
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
            Arg.Any<string?>(),
            Arg.Any<string?>());
        await recorder.Received(1).SetCompletedAsync(
            prWideProtocolId,
            "Completed",
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<long?>(),
            Arg.Any<CacheObservabilityStatus>(),
            Arg.Any<long?>(),
            Arg.Any<long?>());
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
                Arg.Any<string?>(),
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
            reasoningEffort: ReviewReasoningEffort.None,
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
                Arg.Any<string?>(),
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
            reasoningEffort: ReviewReasoningEffort.None,
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
            reasoningEffort: ReviewReasoningEffort.None,
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
        runtime.Model.Returns(
            new AiConfiguredModelDto(Guid.NewGuid(), "prwide-model", "prwide-model", [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]));
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
