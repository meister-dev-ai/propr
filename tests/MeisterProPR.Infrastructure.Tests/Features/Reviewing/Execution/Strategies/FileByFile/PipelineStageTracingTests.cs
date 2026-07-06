// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests that the surviving deterministic per-file stages emit per-comment disposition traces, giving the
///     info-strip and confidence-floor steps the same protocol observability as the semantic screening stage.
/// </summary>
public sealed class PipelineStageTracingTests
{
    private static readonly Guid ProtocolId = Guid.NewGuid();

    [Fact]
    public async Task InfoStrip_EmitsInfoStrippedTracePerDroppedComment()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = BuildContext(
            null,
            new ReviewComment("a.cs", 3, CommentSeverity.Info, "nice abstraction"),
            new ReviewComment("a.cs", 5, CommentSeverity.Warning, "real bug"));
        var stage = new FileByFileInfoCommentStripStage(recorder);

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Single(result.ReviewResult!.Comments);
        await recorder.Received(1).RecordReviewStrategyEventAsync(
            ProtocolId,
            Arg.Is<string>(name => name == ReviewProtocolEventNames.CommentInfoStripped),
            Arg.Is<string?>(details => details != null && details.Contains("a.cs", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output == null),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InfoStrip_WithNoInfoComments_EmitsNoTrace()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = BuildContext(null, new ReviewComment("a.cs", 5, CommentSeverity.Warning, "real bug"));
        var stage = new FileByFileInfoCommentStripStage(recorder);

        await stage.ExecuteAsync(context, CancellationToken.None);

        await recorder.DidNotReceive().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(),
            ReviewProtocolEventNames.CommentInfoStripped,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InfoStrip_WithoutRecorder_DoesNotThrow()
    {
        var context = BuildContext(null, new ReviewComment("a.cs", 3, CommentSeverity.Info, "nice abstraction"));
        var stage = new FileByFileInfoCommentStripStage();

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Empty(result.ReviewResult!.Comments);
    }

    [Fact]
    public async Task ConfidenceFloor_EmitsSeverityDowngradedTracePerDowngrade()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var metrics = new ReviewLoopMetrics(0, null, null, 50, 0, 0, 1);
        var context = BuildContext(metrics, new ReviewComment("a.cs", 4, CommentSeverity.Error, "confirmed defect"));
        var options = new AiReviewOptions { ConfidenceFloorError = 80, ConfidenceFloorWarning = 60 };
        var stage = new FileByFileConfidenceFloorStage(options, recorder);

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(CommentSeverity.Warning, result.ReviewResult!.Comments[0].Severity);
        await recorder.Received(1).RecordReviewStrategyEventAsync(
            ProtocolId,
            Arg.Is<string>(name => name == ReviewProtocolEventNames.CommentSeverityDowngraded),
            Arg.Is<string?>(details => details != null
                                       && details.Contains("\"fromSeverity\":\"Error\"", StringComparison.Ordinal)
                                       && details.Contains("\"toSeverity\":\"Warning\"", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output == null),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfidenceFloor_WithNoDowngrade_EmitsNoTrace()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var metrics = new ReviewLoopMetrics(0, null, null, 95, 0, 0, 1);
        var context = BuildContext(metrics, new ReviewComment("a.cs", 4, CommentSeverity.Error, "confirmed defect"));
        var options = new AiReviewOptions { ConfidenceFloorError = 80, ConfidenceFloorWarning = 60 };
        var stage = new FileByFileConfidenceFloorStage(options, recorder);

        await stage.ExecuteAsync(context, CancellationToken.None);

        await recorder.DidNotReceive().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(),
            ReviewProtocolEventNames.CommentSeverityDowngraded,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private static PerFileReviewContext BuildContext(ReviewLoopMetrics? metrics, params ReviewComment[] comments)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var file = new ChangedFile("a.cs", ChangeType.Edit, "content", "@@ -1 +1 @@\n+line");
        var reviewContext = new ReviewSystemContext(null, [], null) { LoopMetrics = metrics };

        return new PerFileReviewContext(
            job,
            file,
            null,
            reviewContext,
            ProtocolId,
            null,
            new ReviewResult("Base summary.", comments));
    }
}
