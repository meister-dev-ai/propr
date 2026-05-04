// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class FileByFileReviewOrchestratorPrVerificationTests
{
    [Fact]
    public async Task ReviewAsync_SupportedCrossFileConcern_IsPublishedAfterPrVerification()
    {
        const string synthesisJson =
            """
            {
              "summary": "Base summary.",
              "cross_cutting_concerns": [
                {
                  "message": "Missing DI registration in multiple files.",
                  "severity": "warning",
                  "category": "cross_cutting",
                  "candidateSummaryText": "Potential DI registration gap spans multiple files.",
                  "supportingFindingIds": ["finding-pf-001", "finding-pf-002"],
                  "supportingFiles": ["src/Foo.cs", "src/Bar.cs"],
                  "evidenceResolutionState": "missing",
                  "evidenceSource": "synthesis_payload"
                }
              ]
            }
            """;

        const string prVerificationJson =
            """
            {
              "verdict": "supported",
              "recommended_disposition": "Publish",
              "reason_codes": ["verified_bounded_claim_support"],
              "summary": "The retrieved repository evidence directly supports the cross-file claim."
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed local issue.")]));

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ChangedFileSummary("src/Foo.cs", ChangeType.Edit),
                new ChangedFileSummary("src/Bar.cs", ChangeType.Edit),
            ]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string>(0);
                return path switch
                {
                    "src/Foo.cs" => "services.AddFoo();",
                    "src/Bar.cs" => "services.AddBar();",
                    _ => string.Empty,
                };
            });
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorKnowledgeAnswerDto(
                    "ok",
                    [
                        new ProCursorKnowledgeAnswerMatchDto(
                            Guid.NewGuid(),
                            ProCursorSourceKind.Repository,
                            Guid.NewGuid(),
                            "feature/x",
                            "abc123",
                            "docs/knowledge.md",
                            "Service registrations",
                            "Service registrations are split across multiple files.",
                            "knowledge",
                            0.92,
                            "fresh"),
                    ],
                    "Evidence found."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.AddTokensAsync(
                Arg.Any<Guid>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff")]);

        var storedResults = new List<ReviewFileResult>();
        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storedResults.Add(call.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, prVerificationJson))
                {
                    Usage = new UsageDetails { InputTokenCount = 41, OutputTokenCount = 13 },
                });

        var systemContext = new ReviewSystemContext(null, [], reviewTools)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = "test-model",
            ModelId = "test-model",
            Temperature = 0.25f,
        };

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new DomainReviewInvariantFactProvider()],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier(),
            reviewEvidenceCollector: new ReviewContextEvidenceCollector());

        var result = await sut.ReviewAsync(job, pr, systemContext, CancellationToken.None);

        Assert.Contains(result.Comments, comment => comment.Message == "Missing DI registration in multiple files.");
        Assert.DoesNotContain("Potential DI registration gap spans multiple files.", result.Summary, StringComparison.Ordinal);
        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.VerificationPrDecision,
                Arg.Is<string?>(details => details != null && details.Contains("verifierFamilies", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null &&
                    output.Contains("\"recommendedDisposition\":\"Publish\"", StringComparison.Ordinal) &&
                    output.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
        await protocolRecorder.Received()
            .AddTokensAsync(
                Arg.Any<Guid>(),
                41,
                13,
                AiConnectionModelCategory.Default,
                "test-model",
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_UnresolvedCrossFileConcern_IsSummaryOnlyAfterPrVerification()
    {
        const string synthesisJson =
            """
            {
              "summary": "Base summary.",
              "cross_cutting_concerns": [
                {
                  "message": "Missing DI registration in multiple files.",
                  "severity": "warning",
                  "category": "cross_cutting",
                  "candidateSummaryText": "Potential DI registration gap spans multiple files.",
                  "supportingFindingIds": ["finding-pf-001", "finding-pf-002"],
                  "supportingFiles": ["src/Foo.cs", "src/Bar.cs"],
                  "evidenceResolutionState": "missing",
                  "evidenceSource": "synthesis_payload"
                }
              ]
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed local issue.")]));

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ChangedFileSummary("src/Foo.cs", ChangeType.Edit),
                new ChangedFileSummary("src/Bar.cs", ChangeType.Edit),
            ]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("no_result", [], "No evidence."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff")]);

        var storedResults = new List<ReviewFileResult>();
        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storedResults.Add(call.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new DomainReviewInvariantFactProvider()],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier(),
            reviewEvidenceCollector: new ReviewContextEvidenceCollector());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], reviewTools), CancellationToken.None);

        Assert.DoesNotContain(result.Comments, comment => comment.Message == "Missing DI registration in multiple files.");
        Assert.Contains("Potential DI registration gap spans multiple files.", result.Summary);
        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.VerificationEvidenceCollected,
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("coverageState", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.VerificationPrDecision,
                Arg.Is<string?>(details => details != null && details.Contains("verifierFamilies", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null && output.Contains("SummaryOnly", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_EvidenceDrivenCrossFileClaim_DoesNotFailStageValidation()
    {
        const string synthesisJson =
            """
            {
              "summary": "Base summary.",
              "cross_cutting_concerns": [
                {
                  "message": "Missing DI registration in multiple files.",
                  "severity": "warning",
                  "category": "architecture",
                  "candidateSummaryText": "Potential DI registration gap spans multiple files.",
                  "supportingFindingIds": ["finding-pf-001", "finding-pf-002"],
                  "supportingFiles": ["src/Foo.cs", "src/Bar.cs"],
                  "evidenceResolutionState": "missing",
                  "evidenceSource": "synthesis_payload"
                }
              ]
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed local issue.")]));

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ChangedFileSummary("src/Foo.cs", ChangeType.Edit),
                new ChangedFileSummary("src/Bar.cs", ChangeType.Edit),
            ]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("no_result", [], "No evidence."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff")]);

        var storedResults = new List<ReviewFileResult>();
        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storedResults.Add(call.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new DomainReviewInvariantFactProvider()],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier(),
            reviewEvidenceCollector: new ReviewContextEvidenceCollector());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], reviewTools), CancellationToken.None);

        Assert.DoesNotContain(result.Comments, comment => comment.Message == "Missing DI registration in multiple files.");
        Assert.Contains("Potential DI registration gap spans multiple files.", result.Summary);
    }

    private static ReviewJob BuildJobWithResults(ReviewJob original, IEnumerable<ReviewFileResult> results)
    {
        var job = new ReviewJob(
            original.Id,
            original.ClientId,
            original.OrganizationUrl,
            original.ProjectId,
            original.RepositoryId,
            original.PullRequestId,
            original.IterationId);

        foreach (var result in results)
        {
            job.FileReviewResults.Add(result);
        }

        return job;
    }
}
