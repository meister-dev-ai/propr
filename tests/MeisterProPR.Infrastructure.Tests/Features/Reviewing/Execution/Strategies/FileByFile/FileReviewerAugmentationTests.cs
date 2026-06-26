// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for the high-risk zero-finding "second look" augmentation pass in <see cref="FileReviewer" />:
///     it must record to its own protocol pass (Bug B) and honor the client's ProRV opt-out (Bug A).
/// </summary>
public sealed class FileReviewerAugmentationTests
{
    private readonly IAiReviewCore _aiCore = Substitute.For<IAiReviewCore>();
    private readonly IJobRepository _jobRepository = Substitute.For<IJobRepository>();
    private readonly IProtocolRecorder _recorder = Substitute.For<IProtocolRecorder>();
    private ReviewSystemContext? _capturedContext;

    private FileReviewer CreateReviewer(ReviewLoopMetrics metrics)
    {
        // aiCore is the only collaborator on the augmentation path that produces output; it captures the
        // per-file context (so we can assert protocol threading + ProRV gating) and stamps loop metrics.
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.ArgAt<ReviewSystemContext>(1);
                this._capturedContext = ctx;
                ctx.LoopMetrics = metrics;
                return new ReviewResult("no findings", []);
            });

        // All optional collaborators are null — the augmentation path is null-tolerant (dispatch + relevance
        // + memory + verification stages each guard on null), so the run reaches aiCore and completes.
        return new FileReviewer(
            this._aiCore,
            this._recorder,
            this._jobRepository,
            new AiReviewOptions(),
            NullLogger<FileByFileReviewOrchestrator>.Instance,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static (ReviewJob job, PullRequest pr, ChangedFile file) Fixture()
    {
        var file = new ChangedFile("Program.cs", ChangeType.Edit, "full content", "+ changed line");
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 32, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 32, 1, "PR", null, "feature/x", "main",
            new List<ChangedFile> { file }.AsReadOnly());
        return (job, pr, file);
    }

    [Fact]
    public async Task ReviewAugmentationAsync_RecordsToOwnProtocolPass()
    {
        var augmentationProtocolId = Guid.NewGuid();
        this._recorder
            .BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(augmentationProtocolId);

        var reviewer = this.CreateReviewer(new ReviewLoopMetrics(2, null, null, 80, 109708, 1021, 5));
        var (job, pr, file) = Fixture();
        var baseContext = new ReviewSystemContext(null, [], null) { DefaultReviewChatClient = Substitute.For<IChatClient>() };

        await reviewer.ReviewAugmentationAsync(job, pr, file, 0, 1, baseContext, Substitute.For<IChatClient>(), CancellationToken.None);

        // Bug B: the augmentation opened its own protocol pass and threaded the id into the review context.
        // Pass identity: the augmentation pass is recorded as ProRVAugmentation.
        await this._recorder.Received(1).BeginAsync(
            job.Id, Arg.Any<int>(), file.Path, default, Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), ReviewPassKind.ProRVAugmentation, Arg.Any<string?>());
        Assert.NotNull(this._capturedContext);
        Assert.Equal(augmentationProtocolId, this._capturedContext!.ActiveProtocolId);

        // Bug B: the pass is completed with the loop-metric token totals, so the job aggregate includes them.
        await this._recorder.Received(1).SetCompletedAsync(
            augmentationProtocolId, "Completed", 109708, 1021,
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>(),
            Arg.Any<long?>(), Arg.Any<CacheObservabilityStatus>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewAugmentationAsync_HonorsClientProRvOptOut(bool clientEnableProRv)
    {
        this._recorder
            .BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());

        var reviewer = this.CreateReviewer(new ReviewLoopMetrics(0, null, null, null, 100, 10, 1));
        var (job, pr, file) = Fixture();
        var baseContext = new ReviewSystemContext(null, [], null)
        {
            EnableProRV = clientEnableProRv,
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
        };

        await reviewer.ReviewAugmentationAsync(job, pr, file, 0, 1, baseContext, Substitute.For<IChatClient>(), CancellationToken.None);

        // Bug A: the ProRVAugmentation pass must honor the client's ProRV setting — it no longer force-enables
        // ProRV. With opt-out the second look still runs (aiCore is invoked) but the ProRV prefilter is gated off.
        Assert.NotNull(this._capturedContext);
        Assert.Equal(clientEnableProRv, this._capturedContext!.EnableProRV);
        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAugmentationAsync_OnFailure_CompletesPassAsFailedAndRethrows()
    {
        var augmentationProtocolId = Guid.NewGuid();
        this._recorder
            .BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(augmentationProtocolId);

        var reviewer = this.CreateReviewer(new ReviewLoopMetrics(0, null, null, null, 0, 0, 0));
        // The second-look AI call throws (e.g. a transient provider error).
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider error"));

        var (job, pr, file) = Fixture();
        var baseContext = new ReviewSystemContext(null, [], null) { DefaultReviewChatClient = Substitute.For<IChatClient>() };

        // The exception still propagates (job-abort behavior unchanged)...
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reviewer.ReviewAugmentationAsync(job, pr, file, 0, 1, baseContext, Substitute.For<IChatClient>(), CancellationToken.None));

        // ...but the augmentation pass is closed as Failed rather than left open (no leak).
        await this._recorder.Received(1).SetCompletedAsync(
            augmentationProtocolId, "Failed", Arg.Any<long>(), Arg.Any<long>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>(),
            Arg.Any<long?>(), Arg.Any<CacheObservabilityStatus>());
    }
}
