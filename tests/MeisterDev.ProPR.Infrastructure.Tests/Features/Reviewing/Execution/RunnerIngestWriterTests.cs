// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     What an ingested per-file outcome becomes on disk. These rows are the resume checkpoints a reclaimed
///     job reads back, so anything the wire carried but the row lost — findings above all — is anything a
///     resumed review silently publishes without.
/// </summary>
public sealed class RunnerIngestWriterTests
{
    private static readonly Guid JobId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IProtocolRecorder _protocols = Substitute.For<IProtocolRecorder>();

    private static ReviewJob Job(params ReviewFileResult[] rows)
    {
        var job = new ReviewJob(JobId, Guid.NewGuid(), "https://host.invalid", "proj", "repo", 12, 1);
        foreach (var row in rows)
        {
            job.FileReviewResults.Add(row);
        }

        return job;
    }

    private RunnerIngestWriter CreateWriter(ReviewJob job)
    {
        this._jobs.GetByIdWithFileResultsAsync(JobId, Arg.Any<CancellationToken>()).Returns(job);
        return new RunnerIngestWriter(this._jobs, this._protocols);
    }

    // The comments are the checkpoint's substance: without them a reclaimed job's synthesis reasons over
    // finished files that appear to have found nothing, and the published review drops the findings.
    [Fact]
    public async Task ACompletedOutcome_PersistsItsComments()
    {
        ReviewFileResult? written = null;
        this._jobs.AddFileResultAsync(Arg.Do<ReviewFileResult>(result => written = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var writer = this.CreateWriter(Job());

        await writer.WriteFileResultsAsync(
            JobId,
            [
                new RunnerFileOutcome(
                    "src/a.cs", true, false, "looks fine", null, ["pass-1"],
                    [new ReviewComment("src/a.cs", 3, CommentSeverity.Warning, "Bounded?")]),
            ]);

        Assert.NotNull(written);
        Assert.True(written!.IsComplete);
        var comment = Assert.Single(written.Comments!);
        Assert.Equal("Bounded?", comment.Message);
        Assert.Equal(3, comment.LineNumber);
    }

    [Fact]
    public async Task AnExcludedOutcome_IsPersistedAsExcluded()
    {
        ReviewFileResult? written = null;
        this._jobs.AddFileResultAsync(Arg.Do<ReviewFileResult>(result => written = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var writer = this.CreateWriter(Job());

        await writer.WriteFileResultsAsync(
            JobId,
            [new RunnerFileOutcome("src/generated.cs", false, false, null, null, [], IsExcluded: true, ExclusionReason: "**/generated/**")]);

        Assert.NotNull(written);
        Assert.True(written!.IsExcluded);
        Assert.Equal("**/generated/**", written.ExclusionReason);
    }

    // A file that failed on one attempt and succeeded on the next arrives as two outcomes across batches.
    // The entity refuses to complete a failed row, so the writer has to clear the earlier mark first.
    [Fact]
    public async Task AFailedRow_IsUpgradedByARetrysCompletion()
    {
        var failed = new ReviewFileResult(JobId, "src/a.cs");
        failed.MarkFailed("the model refused");
        var writer = this.CreateWriter(Job(failed));

        await writer.WriteFileResultsAsync(
            JobId,
            [
                new RunnerFileOutcome(
                    "src/a.cs", true, false, "second try worked", null, ["pass-1"],
                    [new ReviewComment("src/a.cs", 1, CommentSeverity.Suggestion, "nit")]),
            ]);

        await this._jobs.Received(1).UpdateFileResultAsync(failed, Arg.Any<CancellationToken>());
        Assert.True(failed.IsComplete);
        Assert.False(failed.IsFailed);
        Assert.Single(failed.Comments!);
    }

    // Pricing is per physical model: a spend protocol carrying only the logical name priced against
    // nothing, and a remote review's cost stayed null however much it spent.
    [Fact]
    public async Task AnIngestedSpendRecord_CarriesThePhysicalModelWhenTheLogicalNameResolves()
    {
        var job = Job();
        this._jobs.GetById(JobId).Returns(job);
        var logicalModels = Substitute.For<MeisterDev.ProPR.Application.Interfaces.ILogicalModelResolver>();
        var runtime = Substitute.For<MeisterDev.ProPR.Application.Interfaces.IResolvedAiChatRuntime>();
        runtime.Model.Returns(
            new MeisterDev.ProPR.Application.DTOs.AiConfiguredModelDto(
                Guid.NewGuid(), "gpt-5-mini", "Reviewer",
                [MeisterDev.Ai.Providers.Enums.AiOperationKind.Chat],
                [MeisterDev.Ai.Providers.Enums.AiProtocolMode.Auto]));
        logicalModels.ResolveChatRuntimeAsync(
                job.ClientId, "reviewer-medium",
                Arg.Any<MeisterDev.ProPR.Application.Interfaces.IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(
                new MeisterDev.ProPR.Application.DTOs.ResolvedLogicalModelChatRuntime(
                    runtime, "reviewer-medium",
                    MeisterDev.ProPR.Domain.Enums.LogicalModelLayer.TenantCatalog,
                    MeisterDev.ProPR.Domain.Enums.ReviewReasoningEffort.None));
        var writer = new RunnerIngestWriter(this._jobs, this._protocols, logicalModels);

        await writer.WriteSpendAsync(JobId, [new RunnerSpendRecord("reviewer-medium", 100, 20, null)]);

        await this._protocols.Received(1).BeginAsync(
            JobId, 1, "runner-relay:reviewer-medium",
            Arg.Any<Guid?>(), Arg.Any<MeisterDev.ProPR.Domain.Enums.AiConnectionModelCategory?>(),
            "gpt-5-mini",
            Arg.Any<CancellationToken>(),
            Arg.Any<MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models.ReviewPassKind?>(), Arg.Any<string?>(),
            "reviewer-medium");
    }

    // Unpriced beats unrecorded: the tokens are already spent, so a binding deleted since dispatch must
    // not cost the job its usage record.
    [Fact]
    public async Task ASpendRecordWhoseLogicalNameNoLongerResolves_IsStillRecordedUnpriced()
    {
        var job = Job();
        this._jobs.GetById(JobId).Returns(job);
        var logicalModels = Substitute.For<MeisterDev.ProPR.Application.Interfaces.ILogicalModelResolver>();
        logicalModels.ResolveChatRuntimeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<MeisterDev.ProPR.Application.Interfaces.IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<MeisterDev.ProPR.Application.DTOs.ResolvedLogicalModelChatRuntime>(_ => throw new InvalidOperationException("binding deleted"));
        var writer = new RunnerIngestWriter(this._jobs, this._protocols, logicalModels);

        await writer.WriteSpendAsync(JobId, [new RunnerSpendRecord("reviewer-medium", 100, 20, null)]);

        await this._protocols.Received(1).BeginAsync(
            JobId, 1, "runner-relay:reviewer-medium",
            Arg.Any<Guid?>(), Arg.Any<MeisterDev.ProPR.Domain.Enums.AiConnectionModelCategory?>(),
            null,
            Arg.Any<CancellationToken>(),
            Arg.Any<MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models.ReviewPassKind?>(), Arg.Any<string?>(),
            "reviewer-medium");
        await this._protocols.Received(1).SetCompletedAsync(Arg.Any<Guid>(), "Completed", 100, 20, 0, 0, null, Arg.Any<CancellationToken>());
    }

    // An excluded row is a checkpoint like a completed one: a replayed batch must not overwrite it, and
    // re-marking it would throw and take the whole batch down, spend and trace included.
    [Fact]
    public async Task AnExcludedRow_IsNotTouchedByAReplay()
    {
        var excluded = new ReviewFileResult(JobId, "src/generated.cs");
        excluded.MarkExcluded("**/generated/**");
        var writer = this.CreateWriter(Job(excluded));

        await writer.WriteFileResultsAsync(
            JobId,
            [new RunnerFileOutcome("src/generated.cs", false, false, null, null, [], IsExcluded: true, ExclusionReason: "**/generated/**")]);

        await this._jobs.DidNotReceive().UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>());
        await this._jobs.DidNotReceive().AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>());
    }
}
