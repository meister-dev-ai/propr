// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AgenticFileByFileReviewOrchestratorTests
{
    [Fact]
    public async Task ReviewAsync_RecordsStrategySelectionEvent()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);

        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "anchor_file_path": "src/Web/Program.cs",
                                              "concerns": ["Check anchor file."],
                                              "changed_areas": ["src/Web/Program.cs"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "No sibling-file investigation required for this anchor file."
                                            }
                                            """)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var context = new ReviewSystemContext(null, [], null)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var result = await sut.ReviewAsync(
            job,
            CreatePr(),
            context,
            CancellationToken.None,
            Substitute.For<IChatClient>());

        Assert.Contains("file summary", result.Summary, StringComparison.Ordinal);
        await protocolRecorder.Received(1).RecordReviewStrategyEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.ReviewStrategySelected,
            Arg.Is<string?>(details => details != null && details.Contains("agentic_file_by_file", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("AgenticFileByFile", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WhenOneFileReviewFails_PreservesSuccessfulFileOutcomesInPartialResult()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var preservedComment = new ReviewComment("a.cs", 3, CommentSeverity.Warning, "Preserved finding from successful file.");
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Is<PullRequest>(request => request.ChangedFiles.Count == 1 && request.ChangedFiles[0].Path == "a.cs"),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("a summary", [preservedComment]));
        aiCore.ReviewAsync(
                Arg.Is<PullRequest>(request => request.ChangedFiles.Count == 1 && request.ChangedFiles[0].Path == "b.cs"),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ReviewResult>(new InvalidOperationException("AI failed for b.cs")));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("a.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("b.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "partial synthesis")));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var ex = await Assert.ThrowsAsync<PartialReviewFailureException>(() =>
            sut.ReviewAsync(
                job,
                CreatePr(
                    new ChangedFile("a.cs", ChangeType.Edit, "class A {}", "+warning"),
                    new ChangedFile("b.cs", ChangeType.Edit, "class B {}", "+failure")),
                new ReviewSystemContext(null, [], null),
                CancellationToken.None,
                chatClient));

        Assert.NotNull(ex.PartialResult);
        var partialResult = ex.PartialResult!;
        Assert.Equal("partial synthesis", partialResult.Summary);
        Assert.Contains(partialResult.Comments, comment => comment.Message == preservedComment.Message);
        Assert.Contains(storedResults, result => result.FilePath == "a.cs" && result.IsComplete && !result.IsFailed);
        Assert.Contains(storedResults, result => result.FilePath == "b.cs" && result.IsFailed && !result.IsComplete);
    }

    private static string CreateNoInvestigationPlan(string filePath)
    {
        return $$"""
                 {
                   "plan_id": "plan-{{filePath}}",
                   "anchor_file_path": "{{filePath}}",
                   "concerns": ["Check {{filePath}}."],
                   "changed_areas": ["{{filePath}}"],
                   "investigation_tasks": [],
                   "no_investigation_reason": "No explicit trigger family required deeper follow-up for this straightforward file."
                 }
                 """;
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

    private static ReviewJob CreateJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);
        return job;
    }

    private static PullRequest CreatePr(params ChangedFile[] files)
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
            files.Length == 0
                ?
                [
                    new ChangedFile("src/Web/Program.cs", ChangeType.Edit, "program content", "+builder.Services.AddFoo();"),
                    new ChangedFile("src/Application/Registration.cs", ChangeType.Edit, "registration content", "+services.AddFoo();"),
                ]
                : files.ToList().AsReadOnly());
    }
}
