// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     What a resuming executor is allowed to read back, and what it gets.
///     <para>
///         A file result carries its findings, so this is the one runner read where answering the wrong
///         caller hands somebody else's review away.
///     </para>
/// </summary>
public sealed class RunnerPriorResultsReaderTests
{
    private static readonly Guid JobId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IReviewFileResultStore _results = Substitute.For<IReviewFileResultStore>();

    public RunnerPriorResultsReaderTests()
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Allow(Guid.NewGuid()));
    }

    [Fact]
    public async Task ACallerHoldingTheLease_GetsWhatTheJobAlreadyReviewed()
    {
        var job = JobWith(
            Completed("src/a.cs", "looks fine", [Finding("src/a.cs")], ["pass-1"]),
            Excluded("src/generated.cs", "matched an exclusion"),
            Failed("src/b.cs", "the model refused"));
        this._results.GetByIdWithFileResultsAsync(JobId, Arg.Any<CancellationToken>()).Returns(job);

        var result = await this.CreateReader().ReadAsync(Call());

        Assert.True(result.IsServed);
        var prior = result.Value!;
        Assert.Equal(3, prior.Count);

        var complete = prior.Single(entry => entry.FilePath == "src/a.cs");
        Assert.True(complete.IsComplete);
        Assert.Equal("looks fine", complete.PerFileSummary);
        Assert.Equal(["pass-1"], complete.ReviewedPassKeys);
        Assert.Single(complete.Comments);

        Assert.True(prior.Single(entry => entry.FilePath == "src/generated.cs").IsExcluded);
        Assert.True(prior.Single(entry => entry.FilePath == "src/b.cs").IsFailed);
    }

    // The findings of a review nobody else is entitled to see. A refusal rather than an empty list, so a
    // caller cannot read "you may not ask" as "there is nothing recorded" and start over.
    [Fact]
    public async Task ACallerWithoutTheLease_IsRefusedRatherThanAnsweredEmpty()
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Refuse(RunnerCallRefusal.NotTheLeaseHolder));

        var result = await this.CreateReader().ReadAsync(Call());

        Assert.False(result.IsServed);
        Assert.Equal(RunnerCallRefusal.NotTheLeaseHolder, result.Refusal);
        await this._results.DidNotReceive().GetByIdWithFileResultsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AJobWithNothingRecorded_IsServedAnEmptyList()
    {
        this._results.GetByIdWithFileResultsAsync(JobId, Arg.Any<CancellationToken>()).Returns(JobWith());

        var result = await this.CreateReader().ReadAsync(Call());

        Assert.True(result.IsServed);
        Assert.Empty(result.Value!);
    }

    private RunnerPriorResultsReader CreateReader()
    {
        return new RunnerPriorResultsReader(this._authorizer, this._results);
    }

    private static RunnerCallContext Call()
    {
        return new RunnerCallContext(JobId, 2, "runner-a");
    }

    private static ReviewJob JobWith(params ReviewFileResult[] results)
    {
        var job = new ReviewJob(JobId, Guid.NewGuid(), "https://forge.invalid", "team", "repo", 12, 1);
        foreach (var result in results)
        {
            job.FileReviewResults.Add(result);
        }

        return job;
    }

    private static ReviewFileResult Completed(string path, string summary, IReadOnlyList<ReviewComment> comments, IReadOnlyList<string> passKeys)
    {
        var result = new ReviewFileResult(JobId, path);
        result.MarkCompleted(summary, comments, passKeys);
        return result;
    }

    private static ReviewFileResult Excluded(string path, string reason)
    {
        var result = new ReviewFileResult(JobId, path);
        result.MarkExcluded(reason);
        return result;
    }

    private static ReviewFileResult Failed(string path, string error)
    {
        var result = new ReviewFileResult(JobId, path);
        result.MarkFailed(error);
        return result;
    }

    private static ReviewComment Finding(string path)
    {
        return new ReviewComment(path, 1, CommentSeverity.Warning, "Bounded?");
    }
}
