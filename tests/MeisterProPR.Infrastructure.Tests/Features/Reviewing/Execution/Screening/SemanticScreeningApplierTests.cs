// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Screening;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Screening;

/// <summary>
///     Tests for <see cref="SemanticScreeningApplier" />, the demote-never-delete screening logic shared by the
///     file-by-file screening stage and the PR-wide native path. Uses a fake screener so classification is
///     deterministic without an embedding model.
/// </summary>
public sealed class SemanticScreeningApplierTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ProtocolId = Guid.NewGuid();

    [Fact]
    public async Task FirmComments_AreKept()
    {
        var applier = new SemanticScreeningApplier(new FakeScreener(_ => Firm()));
        var result = Result(new ReviewComment("a.cs", 1, CommentSeverity.Warning, "firm bug"));

        var screened = await applier.ApplyAsync(result, ClientId, ProtocolId, CancellationToken.None);

        Assert.Single(screened.Comments);
    }

    [Fact]
    public async Task HedgedComment_FoldsToSummary_AndRecordsDisposition()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var applier = new SemanticScreeningApplier(new FakeScreener(_ => Hedged()), recorder);
        var result = Result(new ReviewComment("a.cs", 7, CommentSeverity.Error, "hedged maybe"));

        var screened = await applier.ApplyAsync(result, ClientId, ProtocolId, CancellationToken.None);

        Assert.Empty(screened.Comments);
        Assert.Contains("hedged maybe", screened.Summary, StringComparison.Ordinal);
        Assert.Contains("a.cs:7", screened.Summary, StringComparison.Ordinal);
        await recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(),
            Arg.Is<string>(name => name == ReviewProtocolEventNames.CommentScreeningDisposition),
            Arg.Is<string?>(details => details != null && details.Contains("summary_only", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output == null),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VagueComment_FoldsToSummary_NotDeleted()
    {
        var applier = new SemanticScreeningApplier(new FakeScreener(_ => Vague()));
        var result = Result(new ReviewComment("a.cs", 1, CommentSeverity.Suggestion, "please tidy this"));

        var screened = await applier.ApplyAsync(result, ClientId, ProtocolId, CancellationToken.None);

        Assert.Empty(screened.Comments);
        Assert.Contains("please tidy this", screened.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DegradedScreening_KeepsEveryComment_AndRecordsDegraded()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var applier = new SemanticScreeningApplier(new FakeScreener(_ => CommentScreeningResult.DegradedFirm), recorder);
        var result = Result(
            new ReviewComment("a.cs", 1, CommentSeverity.Suggestion, "would fold if screened"),
            new ReviewComment("a.cs", 2, CommentSeverity.Error, "firm"));

        var screened = await applier.ApplyAsync(result, ClientId, ProtocolId, CancellationToken.None);

        Assert.Equal(2, screened.Comments.Count);
        await recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(),
            Arg.Is<string>(name => name == ReviewProtocolEventNames.CommentScreeningDegraded),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoComments_ReturnsResultUnchanged()
    {
        var applier = new SemanticScreeningApplier(new FakeScreener(_ => Vague()));
        var result = Result();

        var screened = await applier.ApplyAsync(result, ClientId, ProtocolId, CancellationToken.None);

        Assert.Same(result, screened);
    }

    [Fact]
    public async Task AllFirm_ReturnsResultUnchanged()
    {
        var applier = new SemanticScreeningApplier(new FakeScreener(_ => Firm()));
        var result = Result(new ReviewComment("a.cs", 1, CommentSeverity.Warning, "firm"));

        var screened = await applier.ApplyAsync(result, ClientId, ProtocolId, CancellationToken.None);

        Assert.Same(result, screened);
    }

    private static ReviewResult Result(params ReviewComment[] comments)
    {
        return new ReviewResult("Base summary.", comments);
    }

    private static CommentScreeningResult Firm()
    {
        return new CommentScreeningResult(CommentScreeningClass.Firm, 0.9);
    }

    private static CommentScreeningResult Hedged()
    {
        return new CommentScreeningResult(CommentScreeningClass.Hedged, 0.8);
    }

    private static CommentScreeningResult Vague()
    {
        return new CommentScreeningResult(CommentScreeningClass.Vague, 0.8);
    }

    private sealed class FakeScreener(Func<string, CommentScreeningResult> classify) : ISemanticCommentScreener
    {
        public Task<CommentScreeningResult> ClassifyAsync(string commentText, Guid clientId, CancellationToken ct = default)
        {
            return Task.FromResult(classify(commentText));
        }
    }
}
