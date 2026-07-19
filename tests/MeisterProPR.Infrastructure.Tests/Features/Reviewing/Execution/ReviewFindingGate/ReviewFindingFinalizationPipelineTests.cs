// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.ReviewFindingGate;

public sealed class ReviewFindingFinalizationPipelineTests
{
    private static CandidateReviewFinding ErrorFinding(string findingId, ReviewCommentReadGrounding grounding)
    {
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
            CommentSeverity.Error,
            "Potential null dereference.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12)
        {
            ReadGrounding = grounding,
            ScopeRelation = ChangedLineRelation.OutsideChange,
        };
    }

    private static FinalGateDecision Publish(string findingId)
    {
        return new FinalGateDecision(
            findingId,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.DefaultPublish],
            "default_publish_rules",
            [],
            null,
            null);
    }

    [Fact]
    public async Task ApplyAsync_FoldsRereadCheckAndRecordsObservations()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var pipeline = new ReviewFindingFinalizationPipeline([new RereadFinalizationCheck()], recorder);
        var protocolId = Guid.NewGuid();

        var findings = new[]
        {
            ErrorFinding("finding-a", ReviewCommentReadGrounding.NotRead),
            ErrorFinding("finding-b", ReviewCommentReadGrounding.CitedLineMissing),
        };
        var baseDecisions = new[] { Publish("finding-a"), Publish("finding-b") };

        var decisions = await pipeline.ApplyAsync(findings, baseDecisions, protocolId);

        var unverified = Assert.Single(decisions, d => d.FindingId == "finding-a");
        Assert.Equal(FinalGateDecision.PublishDisposition, unverified.Disposition);
        Assert.Equal(RereadFinalizationCheck.UnverifiedNote, unverified.PublicationNote);

        var contradicted = Assert.Single(decisions, d => d.FindingId == "finding-b");
        Assert.Equal(FinalGateDecision.DropDisposition, contradicted.Disposition);

        await recorder.Received(2).RecordReviewFindingGateEventAsync(
            protocolId,
            Arg.Is<string>(name => name == ReviewProtocolEventNames.FindingFinalizationCheck),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_WithoutProtocolId_DoesNotRecord()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var pipeline = new ReviewFindingFinalizationPipeline([new RereadFinalizationCheck()], recorder);

        var findings = new[] { ErrorFinding("finding-a", ReviewCommentReadGrounding.NotRead) };

        await pipeline.ApplyAsync(findings, [Publish("finding-a")], null);

        await recorder.DidNotReceive().RecordReviewFindingGateEventAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_PassesThroughDecisionsWithNoMatchingFinding()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var pipeline = new ReviewFindingFinalizationPipeline([new RereadFinalizationCheck()], recorder);

        var orphan = Publish("finding-orphan");

        var decisions = await pipeline.ApplyAsync([], [orphan], Guid.NewGuid());

        Assert.Same(orphan, Assert.Single(decisions));
    }

    [Fact]
    public async Task ApplyAsync_WithNoChecks_ReturnsBaseDecisionsUnchanged()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var pipeline = new ReviewFindingFinalizationPipeline([], recorder);
        var baseDecisions = new[] { Publish("finding-a") };

        var decisions = await pipeline.ApplyAsync(
            [ErrorFinding("finding-a", ReviewCommentReadGrounding.NotRead)],
            baseDecisions,
            Guid.NewGuid());

        Assert.Same(baseDecisions, decisions);
    }
}
