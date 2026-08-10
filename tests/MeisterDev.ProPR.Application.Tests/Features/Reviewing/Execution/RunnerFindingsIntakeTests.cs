// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerFindingsIntakeTests
{
    private static readonly Guid JobId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly RunnerCallContext Call = new(JobId, 3, "runner-a");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IReviewResultPublisher _publisher = Substitute.For<IReviewResultPublisher>();

    private static ReviewComment Comment(string path)
    {
        return new ReviewComment(path, 1, CommentSeverity.Suggestion, $"finding in {path}");
    }

    private static RunnerFindingsChunk Chunk(
        int index = 0,
        int count = 1,
        string submissionId = "sub-1",
        string? summary = "done",
        params string[] paths)
    {
        return new RunnerFindingsChunk(
            submissionId,
            index,
            count,
            summary,
            [.. (paths.Length == 0 ? ["src/a.cs"] : paths).Select(Comment)]);
    }


    // What the review says about itself must survive the wire, or a soft-capped or context-degraded
    // remote review reads as complete everywhere the labels are read.
    [Fact]
    public async Task TheResultsAnnotations_SurviveIntoPublication()
    {
        ReviewResult? published = null;
        this._publisher.PublishAsync(Arg.Any<Guid>(), Arg.Do<ReviewResult>(result => published = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var chunk = Chunk() with
        {
            Annotations = new RunnerResultAnnotations(
                ["src/old.cs"],
                2,
                ["src/big.cs"],
                ["src/huge.cs"],
                BudgetSoftCapped: true,
                BudgetSoftCapThresholdUsd: null,
                BudgetSoftCapSpentUsd: null,
                BudgetSoftCapSkippedFilePaths: ["src/late.cs"]),
        };

        await this.CreateIntake().SubmitAsync(Call, chunk);

        Assert.NotNull(published);
        Assert.True(published!.BudgetSoftCapped);
        Assert.Equal(["src/late.cs"], published.BudgetSoftCapSkippedFilePaths);
        Assert.Equal(["src/old.cs"], published.CarriedForwardFilePaths);
        Assert.Equal(2, published.CarriedForwardCandidatesSkipped);
        Assert.Equal(["src/big.cs"], published.ContextDegradedFilePaths);
        Assert.Equal(["src/huge.cs"], published.ContextSkippedFilePaths);
    }

    // The executor only ever sees the wind-down verdict — the figures live in the budget scope the control
    // plane holds, where the completions were priced. Published without them, the job never gets its
    // budget block, and the paid resume that block gates re-bills the whole review.
    [Fact]
    public async Task AFigurelessSoftCap_GetsItsFiguresFromTheHeldBudget()
    {
        var scope = new BudgetScope(
            new BudgetCaps(null, null, null, null, 1m, null),
            new ReviewSpendBaseline(ReviewScopeSpend.None, ReviewScopeSpend.None, new ReviewScopeSpend(5m, false)));
        Assert.True(scope.IsIncrementSoftCapReached());
        this._budgets.Register(JobId, scope);
        ReviewResult? published = null;
        this._publisher.PublishAsync(Arg.Any<Guid>(), Arg.Do<ReviewResult>(result => published = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var chunk = Chunk() with
        {
            Annotations = new RunnerResultAnnotations(
                [], 0, [], [],
                BudgetSoftCapped: true,
                BudgetSoftCapThresholdUsd: null,
                BudgetSoftCapSpentUsd: null,
                BudgetSoftCapSkippedFilePaths: []),
        };

        await this.CreateIntake().SubmitAsync(Call, chunk);

        Assert.NotNull(published);
        Assert.Equal(1m, published!.BudgetSoftCapThresholdUsd);
        Assert.Equal(5m, published.BudgetSoftCapSpentUsd);
    }

    // Figures the submission already carries are its own; the held scope only fills silence.
    [Fact]
    public async Task ASubmissionCarryingItsOwnFigures_KeepsThem()
    {
        var scope = new BudgetScope(
            new BudgetCaps(null, null, null, null, 1m, null),
            new ReviewSpendBaseline(ReviewScopeSpend.None, ReviewScopeSpend.None, new ReviewScopeSpend(5m, false)));
        Assert.True(scope.IsIncrementSoftCapReached());
        this._budgets.Register(JobId, scope);
        ReviewResult? published = null;
        this._publisher.PublishAsync(Arg.Any<Guid>(), Arg.Do<ReviewResult>(result => published = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var chunk = Chunk() with
        {
            Annotations = new RunnerResultAnnotations(
                [], 0, [], [],
                BudgetSoftCapped: true,
                BudgetSoftCapThresholdUsd: 9m,
                BudgetSoftCapSpentUsd: 8m,
                BudgetSoftCapSkippedFilePaths: []),
        };

        await this.CreateIntake().SubmitAsync(Call, chunk);

        Assert.NotNull(published);
        Assert.Equal(9m, published!.BudgetSoftCapThresholdUsd);
        Assert.Equal(8m, published.BudgetSoftCapSpentUsd);
    }

    // An older runner sends no annotations, and the result must read exactly as it did before the field
    // existed rather than gaining labels that overwrite nothing with something.
    [Fact]
    public async Task ASubmissionWithoutAnnotations_PublishesUnlabelled()
    {
        ReviewResult? published = null;
        this._publisher.PublishAsync(Arg.Any<Guid>(), Arg.Do<ReviewResult>(result => published = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await this.CreateIntake().SubmitAsync(Call, Chunk());

        Assert.NotNull(published);
        Assert.False(published!.BudgetSoftCapped);
        Assert.Empty(published.CarriedForwardFilePaths);
    }

    // The scope the control plane holds open for a job is the relay's proof it owns the call. It has to
    // go when the job does, or a replica accumulates one for every job it ever dispatched.
    [Fact]
    public async Task APublishedJob_IsNoLongerHeldOpen()
    {
        var jobId = Guid.NewGuid();
        this._budgets.Register(jobId, new BudgetScope(BudgetCaps.None, EmptyBaseline));

        var result = await this.CreateIntake().SubmitAsync(
            new RunnerCallContext(jobId, 1, "runner-a"),
            new RunnerFindingsChunk("submission-1", 0, 1, "summary", []));

        Assert.Equal(RunnerSubmissionOutcome.Published, result.Outcome);
        Assert.Null(this._budgets.Find(jobId));
    }

    private static ReviewSpendBaseline EmptyBaseline { get; } =
        new(ReviewScopeSpend.None, ReviewScopeSpend.None, ReviewScopeSpend.None);

    private readonly RunnerJobBudgetRegistry _budgets = new();
    private readonly RunnerSubmissionLedger _ledger = new();
    private readonly RunnerRelayReplayCache _replays = new();
    private readonly RunnerJobToolsRegistry _tools = new();
    private readonly RunnerWorkspaceRegistry _workspaces = new();

    private RunnerFindingsIntake CreateIntake(bool authorized = true, RunnerCallRefusal refusal = RunnerCallRefusal.SupersededGeneration)
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                authorized
                    ? RunnerCallAuthorization.Allow(Guid.NewGuid())
                    : RunnerCallAuthorization.Refuse(refusal));

        return new RunnerFindingsIntake(this._authorizer, this._publisher, this._budgets, this._tools, this._ledger, this._replays, this._workspaces);
    }

    // The service is scoped to one HTTP request and each chunk of a submission is a request of its own,
    // so assembly held on the service assembled nothing, ever: every chunk landed on a fresh instance,
    // the review never published, and the job sat Processing until its lease expired.
    [Fact]
    public async Task ChunksArrivingOnDifferentIntakeInstances_StillAssemble()
    {
        var first = await this.CreateIntake().SubmitAsync(Call, Chunk(0, 2, summary: null, paths: "src/a.cs"));
        var last = await this.CreateIntake().SubmitAsync(Call, Chunk(1, 2, summary: "all done", paths: "src/b.cs"));

        Assert.Equal(RunnerSubmissionOutcome.AwaitingChunks, first.Outcome);
        Assert.Equal(RunnerSubmissionOutcome.Published, last.Outcome);
        await this._publisher.Received(1).PublishAsync(JobId, Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    // The publish-once guard exists for the resend, and the resend arrives on a different request by
    // definition — a runner retries after its first POST timed out server-side but published anyway.
    [Fact]
    public async Task AResendOnADifferentIntakeInstance_PostsNothingFurther()
    {
        await this.CreateIntake().SubmitAsync(Call, Chunk());

        var resend = await this.CreateIntake().SubmitAsync(Call, Chunk());

        Assert.Equal(RunnerSubmissionOutcome.AlreadyPublished, resend.Outcome);
        await this._publisher.Received(1).PublishAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASingleChunkSubmission_GoesStraightToPublication()
    {
        var result = await this.CreateIntake().SubmitAsync(Call, Chunk());

        Assert.Equal(RunnerSubmissionOutcome.Published, result.Outcome);
        await this._publisher.Received(1).PublishAsync(JobId, Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    // Publishing what has arrived so far would post half a review and leave no way to tell that happened.
    [Fact]
    public async Task APartialSubmission_PublishesNothing()
    {
        var intake = this.CreateIntake();

        var first = await intake.SubmitAsync(Call, Chunk(index: 0, count: 3, summary: null, paths: "src/a.cs"));

        Assert.Equal(RunnerSubmissionOutcome.AwaitingChunks, first.Outcome);
        Assert.Equal(2, first.MissingChunks);
        await this._publisher.DidNotReceive().PublishAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheLastChunk_PublishesEverySubmittedFinding()
    {
        var intake = this.CreateIntake();
        ReviewResult? published = null;
        await this._publisher.PublishAsync(JobId, Arg.Do<ReviewResult>(r => published = r), Arg.Any<CancellationToken>());

        await intake.SubmitAsync(Call, Chunk(0, 2, summary: null, paths: "src/a.cs"));
        var last = await intake.SubmitAsync(Call, Chunk(1, 2, summary: "all done", paths: "src/b.cs"));

        Assert.Equal(RunnerSubmissionOutcome.Published, last.Outcome);
        Assert.NotNull(published);
        Assert.Equal(2, published!.Comments.Count);
        Assert.Equal("all done", published.Summary);
    }

    // Posting the same review twice is the one thing that must not happen, so a resend publishes nothing
    // and still answers as success.
    [Fact]
    public async Task AResendOfAPublishedSubmission_PostsNothingFurther()
    {
        var intake = this.CreateIntake();
        await intake.SubmitAsync(Call, Chunk());

        var resend = await intake.SubmitAsync(Call, Chunk());

        Assert.Equal(RunnerSubmissionOutcome.AlreadyPublished, resend.Outcome);
        Assert.True(resend.IsFinished);
        await this._publisher.Received(1).PublishAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADifferentSubmissionAfterOnePublished_IsRejectedRatherThanPostedOnTop()
    {
        var intake = this.CreateIntake();
        await intake.SubmitAsync(Call, Chunk(submissionId: "sub-1"));

        var second = await intake.SubmitAsync(Call, Chunk(submissionId: "sub-2"));

        Assert.Equal(RunnerSubmissionOutcome.Rejected, second.Outcome);
        await this._publisher.Received(1).PublishAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    // The case the story names: the original executor comes back after a reclaim and submits. Its
    // generation is behind, and letting it through would publish a second review of the same job.
    [Fact]
    public async Task ASubmissionFromASupersededGeneration_IsRefused()
    {
        var result = await this.CreateIntake(authorized: false).SubmitAsync(Call, Chunk());

        Assert.Equal(RunnerSubmissionOutcome.NotAuthorized, result.Outcome);
        Assert.Equal(RunnerCallRefusal.SupersededGeneration, result.CallRefusal);
        await this._publisher.DidNotReceive().PublishAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AChunkFromAnotherSubmission_DoesNotContaminateTheOneBeingAssembled()
    {
        var intake = this.CreateIntake();
        await intake.SubmitAsync(Call, Chunk(0, 2, submissionId: "sub-1", summary: null));

        var foreign = await intake.SubmitAsync(Call, Chunk(1, 2, submissionId: "sub-other", summary: null));

        Assert.Equal(RunnerSubmissionOutcome.Rejected, foreign.Outcome);
        await this._publisher.DidNotReceive().PublishAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    public async Task AChunkOutsideItsDeclaredRange_IsRejectedBeforeAnythingElse(int index, int count)
    {
        var result = await this.CreateIntake().SubmitAsync(
            Call,
            new RunnerFindingsChunk("sub-1", index, count, null, []));

        Assert.Equal(RunnerSubmissionOutcome.Rejected, result.Outcome);
        await this._authorizer.DidNotReceive().AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>());
    }

    // A publication that threw has not published. Keeping the claim would strand a review that never
    // posted and refuse every retry.
    [Fact]
    public async Task WhenPublicationFails_ARetryIsStillAllowed()
    {
        var intake = this.CreateIntake();
        this._publisher.PublishAsync(JobId, Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("provider down"), _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => intake.SubmitAsync(Call, Chunk()));
        var retry = await intake.SubmitAsync(Call, Chunk());

        Assert.Equal(RunnerSubmissionOutcome.Published, retry.Outcome);
    }
}
