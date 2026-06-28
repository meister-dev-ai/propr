// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     End-to-end coverage that a finding's changed-line scope flows through the real FileByFile pipeline
///     (per-file review → classification at finding construction → final gate → published comment + grounded
///     summary). The diff below changes new-file lines 10-14, so a finding on line 11 is on the change and a
///     finding on line 240 is in pre-existing code outside it.
/// </summary>
public sealed class FileByFileReviewOrchestratorScopeLabelingTests
{
    private const string ChangedFileDiff =
        "@@ -10,4 +10,5 @@\n" +
        " context one\n" +
        " context two\n" +
        "+inserted line\n" +
        " context three\n" +
        " context four\n";

    [Fact]
    public async Task ReviewAsync_FindingOutsideChangedLines_PublishesWithLabelAndSummaryNote()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 240, CommentSeverity.Warning, "Dereferences a possibly-null value in pre-existing code.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", ChangedFileDiff));
        var jobRepo = CreateJobRepository(job, []);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = CreateSut(aiCore, protocolRecorder, jobRepo, chatClient);

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        // The finding is still published (label-only), now carrying the OutsideChange relation end-to-end.
        var published = Assert.Single(result.Comments);
        Assert.Equal("Dereferences a possibly-null value in pre-existing code.", published.Message);
        Assert.Equal(ReviewCommentScopeRelation.OutsideChange, published.ScopeRelation);

        // The grounded summary tells the author the finding is outside their change.
        Assert.Contains("in pre-existing code outside this change", result.Summary, StringComparison.Ordinal);

        // The gate attached the reason code while keeping the Publish disposition.
        await protocolRecorder.Received()
            .RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.ReviewFindingGateDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null
                                          && output.Contains("\"disposition\":\"Publish\"", StringComparison.Ordinal)
                                          && output.Contains("outside_changed_lines", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_FindingOnChangedLine_PublishesWithoutLabelOrSummaryNote()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 11, CommentSeverity.Warning, "Bug on a line this PR changed.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", ChangedFileDiff));
        var jobRepo = CreateJobRepository(job, []);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = CreateSut(aiCore, protocolRecorder, jobRepo, chatClient);

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        var published = Assert.Single(result.Comments);
        Assert.Equal("Bug on a line this PR changed.", published.Message);
        Assert.Equal(ReviewCommentScopeRelation.OnChangedLine, published.ScopeRelation);
        Assert.DoesNotContain("in pre-existing code outside this change", result.Summary, StringComparison.Ordinal);

        await protocolRecorder.DidNotReceive()
            .RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.ReviewFindingGateDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("outside_changed_lines", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
    }

    private static FileByFileReviewOrchestrator CreateSut(
        IAiReviewCore aiCore,
        IProtocolRecorder protocolRecorder,
        IJobRepository jobRepo,
        IChatClient chatClient)
    {
        return new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, MaxFileReviewRetries = 3, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new StubInvariantFactProvider([])]);
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
    }

    private static PullRequest CreatePr(params ChangedFile[] files)
    {
        return new PullRequest(
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
            files.ToList().AsReadOnly());
    }

    private static IJobRepository CreateJobRepository(ReviewJob job, List<ReviewFileResult> storedResults)
    {
        var repository = Substitute.For<IJobRepository>();
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
        return repository;
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

    private static IProtocolRecorder CreateProtocolRecorder()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Task.FromResult(Guid.NewGuid()));
        recorder.SetCompletedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        recorder.RecordAiCallAsync(
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
        recorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return recorder;
    }

    private sealed class StubInvariantFactProvider(IReadOnlyList<InvariantFact> facts) : IReviewInvariantFactProvider
    {
        public IReadOnlyList<InvariantFact> GetFacts()
        {
            return facts;
        }
    }
}
