// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class ReviewContextToolsTests
{
    [Fact]
    public async Task GetFileContentAsync_PathOutsideSeedScope_ReturnsEmptyWithoutCallingInner()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetFileContentToolName],
            maxToolCalls: 2,
            scopedFilePaths: ["src/Allowed.cs"]);

        var result = await sut.GetFileContentAsync("src/Other.cs", "feature/x", 1, 50, CancellationToken.None);

        Assert.Equal(string.Empty, result);
        await inner.DidNotReceive()
            .GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        var attempt = Assert.Single(sut.Attempts);
        Assert.Equal(BoundedReviewContextTools.GetFileContentToolName, attempt.ToolName);
        Assert.Equal(BoundedReviewContextTools.BlockedScopeViolationStatus, attempt.Status);
        Assert.Equal("src/Other.cs", attempt.Target);
    }

    [Fact]
    public async Task AskProCursorKnowledgeAsync_WhenToolNotAllowed_ReturnsBlockedStatus()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetChangedFilesToolName],
            maxToolCalls: 1);

        var result = await sut.AskProCursorKnowledgeAsync("How is DI wired?", CancellationToken.None);

        Assert.Equal(BoundedReviewContextTools.BlockedNotAllowedStatus, result.Status);
        Assert.Empty(result.Results);
        await inner.DidNotReceive().AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        var attempt = Assert.Single(sut.Attempts);
        Assert.Equal(BoundedReviewContextTools.AskProCursorKnowledgeToolName, attempt.ToolName);
        Assert.Equal(BoundedReviewContextTools.BlockedNotAllowedStatus, attempt.Status);
    }

    [Fact]
    public async Task GetChangedFilesAsync_AfterBudgetExhausted_ReturnsEmptyAndRecordsBudgetBlock()
    {
        var inner = Substitute.For<IReviewContextTools>();
        inner.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns([new MeisterProPR.Domain.ValueObjects.ChangedFileSummary("src/Program.cs", MeisterProPR.Domain.Enums.ChangeType.Edit)]);

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetChangedFilesToolName],
            maxToolCalls: 1);

        var first = await sut.GetChangedFilesAsync(CancellationToken.None);
        var second = await sut.GetChangedFilesAsync(CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal(2, sut.Attempts.Count);
        Assert.Equal(BoundedReviewContextTools.SuccessStatus, sut.Attempts[0].Status);
        Assert.Equal(BoundedReviewContextTools.BlockedBudgetExhaustedStatus, sut.Attempts[1].Status);
    }
}
