// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     The maximum review duration of a tenant, as the file-by-file scan observes it. A review that reaches it starts
///     no further files, completes with a synthesis over the files it reviewed, and carries the files it left out on
///     its result, so the summary can name them and the job can record why it ended early.
/// </summary>
public sealed class FileByFileReviewOrchestratorTimeLimitTests
{
    [Fact]
    public async Task AReviewWhoseTimeIsUpWhenTheScanStarts_ReviewsNoFileAndCompletesWithEveryFileListed()
    {
        var aiCore = AiCoreReturning("file summary");
        var (sut, job, pr) = Build(aiCore, "a.cs", "b.cs", "c.cs");
        var context = new ReviewSystemContext(null, [], null) { TimeLimit = new ReviewTimeLimit(600, TimeSpan.Zero) };

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await aiCore.DidNotReceiveWithAnyArgs().ReviewAsync(default!, default!, default);
        Assert.True(result.TimeLimitReached);
        Assert.Equal(600, result.TimeLimitSeconds);
        Assert.Equal(["a.cs", "b.cs", "c.cs"], result.TimeLimitSkippedFilePaths.Order());
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    // The files are reviewed one at a time here. The deadline passes while the first file is being reviewed, so
    // that file is finished and the two queued behind it are not started.
    [Fact]
    public async Task AReviewThatReachesItsTimeLimitDuringAFile_FinishesThatFileAndStartsNoOther()
    {
        var clock = new FakeTimeProvider();
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                clock.Advance(TimeSpan.FromSeconds(601));
                return new ReviewResult(
                    "file summary",
                    [new ReviewComment("a.cs", 3, CommentSeverity.Warning, "The value is never disposed.")]);
            });
        var (sut, job, pr) = Build(aiCore, "a.cs", "b.cs", "c.cs");
        var context = new ReviewSystemContext(null, [], null) { TimeLimit = new ReviewTimeLimit(600, TimeSpan.FromSeconds(600), clock) };

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        Assert.True(result.TimeLimitReached);
        Assert.Equal(["b.cs", "c.cs"], result.TimeLimitSkippedFilePaths.Order());
        Assert.False(result.TimeLimitSkippedPrWidePasses);
    }

    [Fact]
    public async Task AReviewThatFinishesBeforeItsTimeLimit_CarriesNoTimeLimit()
    {
        var aiCore = AiCoreReturning("file summary");
        var (sut, job, pr) = Build(aiCore, "a.cs", "b.cs");
        var context = new ReviewSystemContext(null, [], null) { TimeLimit = new ReviewTimeLimit(600, TimeSpan.FromSeconds(600)) };

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await aiCore.Received(2).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        Assert.False(result.TimeLimitReached);
        Assert.Null(result.TimeLimitSeconds);
        Assert.Empty(result.TimeLimitSkippedFilePaths);
    }

    private static IAiReviewCore AiCoreReturning(string summary)
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(summary, []));
        return aiCore;
    }

    private static (FileByFileReviewOrchestrator Sut, ReviewJob Job, PullRequest Pr) Build(IAiReviewCore aiCore, params string[] paths)
    {
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
            paths.Select(path => new ChangedFile(path, ChangeType.Edit, "content", "diff")).ToList().AsReadOnly());

        // The results the file reviewer stores are the results the synthesis reads back, as the database does.
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                job.FileReviewResults.Add(call.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });

        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            recorder,
            repo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, MaxFileReviewRetries = 3, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>());
        return (sut, job, pr);
    }
}
