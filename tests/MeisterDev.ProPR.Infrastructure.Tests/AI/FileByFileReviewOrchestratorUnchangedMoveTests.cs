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
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Files that a pull request only moves or renames have no changed line. The file-by-file scan does not review
///     them, records each with the move as its reason, leaves them out of the files in scope, and writes their number
///     into the summary.
/// </summary>
public sealed class FileByFileReviewOrchestratorUnchangedMoveTests
{
    private const string EditDiff = "@@ -1,1 +1,1 @@\n-old\n+new\n";

    [Fact]
    public async Task AFileThatIsOnlyMoved_IsNotReviewed_AndIsRecordedWithTheMoveAsItsReason()
    {
        var aiCore = AiCoreReturning("file summary");
        var (sut, job, pr, repo) = Build(
            aiCore,
            new ChangedFile("src/New.cs", ChangeType.Rename, "content", string.Empty, originalPath: "src/Old.cs", contentUnchanged: true),
            new ChangedFile("src/Edited.cs", ChangeType.Edit, "content", EditDiff));

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        await aiCore.Received(1).ReviewAsync(
            Arg.Is<PullRequest>(reviewed => reviewed.ChangedFiles.Any(file => file.Path == "src/Edited.cs")),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>());
        var moved = Assert.Single(job.FileReviewResults, fileResult => fileResult.FilePath == "src/New.cs");
        Assert.True(moved.IsExcluded);
        Assert.Equal("Moved from src/Old.cs without content changes", moved.ExclusionReason);
        await repo.Received().UpdateInScopeChangedFileCountAsync(job.Id, 1, Arg.Any<CancellationToken>());
        Assert.EndsWith(
            "**Moved file not reviewed.** 1 file was only moved or renamed, without changes to its content, so ProPR did not review it.",
            result.Summary);
    }

    [Fact]
    public async Task AFileThatIsMovedAndChanged_IsReviewed()
    {
        var aiCore = AiCoreReturning("file summary");
        var (sut, job, pr, repo) = Build(
            aiCore,
            new ChangedFile("src/New.cs", ChangeType.Rename, "content", EditDiff, originalPath: "src/Old.cs"));

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        await aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(job.FileReviewResults, fileResult => fileResult.IsExcluded);
        await repo.Received().UpdateInScopeChangedFileCountAsync(job.Id, 1, Arg.Any<CancellationToken>());
        Assert.DoesNotContain("Moved file", result.Summary);
    }

    // A pull request that only moves files leaves nothing to review. ProPR still writes the number of moved files
    // into the summary.
    [Fact]
    public async Task APullRequestThatOnlyMovesFiles_ReviewsNoFile_AndStatesTheNumberOfMovedFiles()
    {
        var aiCore = AiCoreReturning("file summary");
        var (sut, job, pr, repo) = Build(
            aiCore,
            new ChangedFile("src/a/One.cs", ChangeType.Rename, "one", string.Empty, originalPath: "src/One.cs", contentUnchanged: true),
            new ChangedFile("src/a/Two.cs", ChangeType.Rename, "two", string.Empty, originalPath: "src/Two.cs", contentUnchanged: true));

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        await aiCore.DidNotReceiveWithAnyArgs().ReviewAsync(default!, default!, default);
        Assert.Equal(2, job.FileReviewResults.Count(fileResult => fileResult.IsExcluded));
        await repo.Received().UpdateInScopeChangedFileCountAsync(job.Id, 0, Arg.Any<CancellationToken>());
        Assert.Contains("2 files were only moved or renamed", result.Summary);
    }

    // A file reviewed in an earlier attempt under another pass configuration is selected again. When it now counts as
    // an unchanged move or matches an exclusion pattern, ProPR reuses its result, because the job holds one result
    // per file and the next dispatch reads them by path.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileWithAResultFromAnotherPassConfiguration_KeepsOneResult_WhenItIsNoLongerReviewed(bool excludedByPattern)
    {
        var aiCore = AiCoreReturning("file summary");
        var file = excludedByPattern
            ? new ChangedFile("generated/Code.cs", ChangeType.Edit, "content", EditDiff)
            : new ChangedFile("src/New.cs", ChangeType.Rename, "content", string.Empty, originalPath: "src/Old.cs", contentUnchanged: true);
        var (sut, job, pr, repo) = Build(aiCore, file);
        var earlier = new ReviewFileResult(job.Id, file.Path);
        earlier.MarkCompleted("earlier summary", [], ["1:earlier-model:-:-:None"]);
        job.FileReviewResults.Add(earlier);
        var context = new ReviewSystemContext(null, [], null)
        {
            ExclusionRules = excludedByPattern ? ReviewExclusionRules.FromPatterns(["generated/**"]) : ReviewExclusionRules.Empty,
        };

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);
        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        var result = Assert.Single(job.FileReviewResults, fileResult => fileResult.FilePath == file.Path);
        Assert.Same(earlier, result);
        Assert.True(result.IsExcluded);
        Assert.Empty(result.ReviewedPassKeys);
        await repo.DidNotReceive().AddFileResultAsync(Arg.Is<ReviewFileResult>(added => added.FilePath == file.Path), Arg.Any<CancellationToken>());
        await repo.Received(1).UpdateFileResultAsync(earlier, Arg.Any<CancellationToken>());
        await aiCore.DidNotReceiveWithAnyArgs().ReviewAsync(default!, default!, default);
    }

    private static IAiReviewCore AiCoreReturning(string summary)
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(summary, []));
        return aiCore;
    }

    private static (FileByFileReviewOrchestrator Sut, ReviewJob Job, PullRequest Pr, IJobRepository Repo) Build(
        IAiReviewCore aiCore,
        params ChangedFile[] files)
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
            files.ToList().AsReadOnly());

        // The results the scan stores are the results the synthesis reads back, as the database does.
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
        return (sut, job, pr, repo);
    }
}
