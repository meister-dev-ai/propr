// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for multi-pass union generation in <see cref="FileReviewer" />: it runs additional independent
///     passes only for in-scope tiers, unions every distinct finding before the single persistence write, records
///     per-pass provenance, and — when the client has not opted in — behaves identically to a single-pass review.
/// </summary>
public sealed class FileReviewerMultiPassUnionTests
{
    // Each AI call returns a distinct concrete finding so the union across passes is observable and nothing
    // is dropped by the per-file filters (Warning severity, no hedge/vague/info phrasing).
    private static readonly IReadOnlyList<ReviewComment> PassComments =
    [
        new("Program.cs", 10, CommentSeverity.Warning, "Null reference risk when config is missing."),
        new("Program.cs", 40, CommentSeverity.Warning, "Resource leak: the stream is never disposed."),
        new("Program.cs", 70, CommentSeverity.Warning, "Off-by-one error in the loop bound."),
        new("Program.cs", 90, CommentSeverity.Warning, "Unchecked cast can throw at runtime."),
    ];

    private readonly IAiReviewCore _aiCore = Substitute.For<IAiReviewCore>();
    private readonly IJobRepository _jobRepository = Substitute.For<IJobRepository>();
    private readonly IProtocolRecorder _recorder = Substitute.For<IProtocolRecorder>();
    private int _aiCallCount;
    private ReviewFileResult? _persistedResult;

    public FileReviewerMultiPassUnionTests()
    {
        this._recorder
            .BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(_ => Guid.NewGuid());

        // Capture the per-file result at the single completion write so tests can inspect the unioned comments.
        this._jobRepository
            .When(r => r.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var result = ci.ArgAt<ReviewFileResult>(0);
                if (result.IsComplete)
                {
                    this._persistedResult = result;
                }
            });
    }

    private FileReviewer CreateReviewer()
    {
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.ArgAt<ReviewSystemContext>(1);
                ctx.LoopMetrics = new ReviewLoopMetrics(0, null, null, 90, 100, 10, 1);
                var comment = PassComments[Math.Min(this._aiCallCount, PassComments.Count - 1)];
                this._aiCallCount++;
                return new ReviewResult("summary", [comment]);
            });

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

    // Builds a changed file whose size heuristic lands on the requested tier (<=30 Low, <=150 Medium, else High).
    private static ChangedFile FileForTier(FileComplexityTier tier)
    {
        var changedLines = tier switch
        {
            FileComplexityTier.Low => 5,
            FileComplexityTier.Medium => 60,
            _ => 200,
        };

        var diff = new StringBuilder("@@ -1,1 +1,1 @@\n");
        for (var i = 0; i < changedLines; i++)
        {
            diff.Append("+ added line ").Append(i).Append('\n');
        }

        return new ChangedFile("Program.cs", ChangeType.Edit, "full content", diff.ToString());
    }

    private static (ReviewJob job, PullRequest pr) Fixture(ChangedFile file)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 32, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 32, 1, "PR", null, "feature/x", "main",
            new List<ChangedFile> { file }.AsReadOnly());
        return (job, pr);
    }

    private ReviewSystemContext MultiPassContext(int passCount)
    {
        return new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            MultiPassUnionPassCount = passCount,
        };
    }

    [Fact]
    public async Task FlagOff_RunsSinglePass_ByteIdenticalToToday()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Medium);
        var (job, pr) = Fixture(file);

        // Flag off — even a Medium file must not fan out.
        var baseContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = false,
            MultiPassUnionPassCount = 3,
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, baseContext, null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        Assert.NotNull(this._persistedResult);
        var comments = this._persistedResult!.Comments!;
        Assert.Single(comments);
        // No union stamping when the flag is off — provenance is exactly as a single-pass review produces.
        Assert.Null(comments[0].OriginPassKind);
        await this._recorder.DidNotReceive().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LowTier_RunsSinglePass_EvenWhenEnabled()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Low);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, this.MultiPassContext(3), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // Tier binding: multi-pass union runs only for Medium/High tiers; a Low-tier file gets exactly one pass.
        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        await this._recorder.DidNotReceive().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(FileComplexityTier.Medium)]
    [InlineData(FileComplexityTier.High)]
    public async Task InScopeTier_FansOutKTimes_AndUnionsDistinctFindings(FileComplexityTier tier)
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(tier);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, this.MultiPassContext(3), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // k=3 fan-out: the baseline pass plus two resamples.
        await this._aiCore.Received(3).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());

        // Union preserves every distinct finding — the three passes each contributed one.
        Assert.NotNull(this._persistedResult);
        var comments = this._persistedResult!.Comments!;
        Assert.Equal(3, comments.Count);
        Assert.Equal(
            new[]
            {
                "Null reference risk when config is missing.",
                "Resource leak: the stream is never disposed.",
                "Off-by-one error in the loop bound.",
            },
            comments.Select(c => c.Message).ToArray());
    }

    [Fact]
    public async Task InScopeTier_RecordsProvenancePerPass()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, this.MultiPassContext(3), null, Substitute.For<IChatClient>(), CancellationToken.None);

        Assert.NotNull(this._persistedResult);
        var comments = this._persistedResult!.Comments!;

        // The baseline pass keeps its natural (unstamped) origin; the two resamples are tagged as union passes.
        Assert.Null(comments[0].OriginPassKind);
        Assert.Equal(nameof(ReviewPassKind.MultiPassUnion), comments[1].OriginPassKind);
        Assert.Equal(nameof(ReviewPassKind.MultiPassUnion), comments[2].OriginPassKind);

        // A single completion event records the per-pass catch counts so funnel attribution keeps working.
        await this._recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
