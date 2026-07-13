// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
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
            2,
            ["src/Allowed.cs"]);

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
            1);

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
            .Returns([new ChangedFileSummary("src/Program.cs", ChangeType.Edit)]);

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetChangedFilesToolName],
            1);

        var first = await sut.GetChangedFilesAsync(CancellationToken.None);
        var second = await sut.GetChangedFilesAsync(CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal(2, sut.Attempts.Count);
        Assert.Equal(BoundedReviewContextTools.SuccessStatus, sut.Attempts[0].Status);
        Assert.Equal(BoundedReviewContextTools.BlockedBudgetExhaustedStatus, sut.Attempts[1].Status);
    }

    [Fact]
    public async Task SearchSourceRepoAsync_WhenToolNotAllowed_ReturnsStructuredBlockedResult()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetChangedFilesToolName],
            1);

        var result = await sut.SearchSourceRepoAsync("service", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.BlockedNotAllowed, result.Status);
        Assert.Equal(RepositorySearchBranchSides.Source, result.BranchSide);
        Assert.Equal(RepositorySearchPathScopes.Repository, result.PathScope);
        Assert.Empty(result.Matches);
        await inner.DidNotReceive().SearchSourceRepoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchSourceChangedFilesAsync_AfterBudgetExhausted_ReturnsStructuredBudgetBlock()
    {
        var inner = Substitute.For<IReviewContextTools>();
        inner.SearchSourceChangedFilesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.ChangedFiles,
                    null,
                    [new RepositorySearchMatch("src/Allowed.cs", 5, "needle")],
                    [],
                    false));

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.SearchSourceChangedFilesToolName],
            1);

        var first = await sut.SearchSourceChangedFilesAsync("needle", null, CancellationToken.None);
        var second = await sut.SearchSourceChangedFilesAsync("needle", null, CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Success, first.Status);
        Assert.Equal(RepositorySearchStatuses.BlockedBudgetExhausted, second.Status);
        Assert.Equal(BoundedReviewContextTools.SuccessStatus, sut.Attempts[0].Status);
        Assert.Equal(BoundedReviewContextTools.BlockedBudgetExhaustedStatus, sut.Attempts[1].Status);
    }

    [Fact]
    public async Task SearchTargetRepoAsync_InnerInvalidRegexResult_PassesStructuredResultThrough()
    {
        var inner = Substitute.For<IReviewContextTools>();
        inner.SearchTargetRepoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    RepositorySearchStatuses.InvalidRequest,
                    RepositorySearchBranchSides.Target,
                    RepositorySearchPathScopes.Repository,
                    "**/*.cs",
                    [],
                    [new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.InvalidRegex, "Invalid pattern")],
                    false));

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.SearchTargetRepoToolName],
            1);

        var result = await sut.SearchTargetRepoAsync("(", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.InvalidRequest, result.Status);
        Assert.Single(result.Limitations);
        Assert.Equal(RepositorySearchLimitationReasons.InvalidRegex, result.Limitations[0].Reason);
    }

    [Fact]
    public async Task SearchTargetChangedFilesAsync_WhenToolNotAllowed_UsesTargetChangedFilesShape()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetFileContentToolName],
            1);

        var result = await sut.SearchTargetChangedFilesAsync("GreetingPrefix", null, CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.BlockedNotAllowed, result.Status);
        Assert.Equal(RepositorySearchBranchSides.Target, result.BranchSide);
        Assert.Equal(RepositorySearchPathScopes.ChangedFiles, result.PathScope);
    }

    [Fact]
    public async Task SearchSourceRepoAsync_InnerSuccessResult_PassesStructuredResultThrough()
    {
        var inner = Substitute.For<IReviewContextTools>();
        inner.SearchSourceRepoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    "**/*.cs",
                    [new RepositorySearchMatch("src/Foo.cs", 12, "needle")],
                    [],
                    false));

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.SearchSourceRepoToolName],
            1);

        var result = await sut.SearchSourceRepoAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Equal(RepositorySearchBranchSides.Source, result.BranchSide);
        Assert.Equal(RepositorySearchPathScopes.Repository, result.PathScope);
        Assert.Single(result.Matches);
        Assert.Equal("src/Foo.cs", result.Matches[0].FilePath);
    }

    [Fact]
    public async Task SearchCodeAsync_WhenToolNotAllowed_ReturnsStructuredBlockedResult()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetChangedFilesToolName],
            1);

        var request = new CodeSearchRequest(
            "needle",
            CodeSearchModes.ExactPhrase,
            RepositorySearchBranchSides.Source,
            RepositorySearchPathScopes.Repository);

        var result = await sut.SearchCodeAsync(request, CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.BlockedNotAllowed, result.Status);
        Assert.Equal(RepositorySearchBranchSides.Source, result.BranchSide);
        Assert.Equal(RepositorySearchPathScopes.Repository, result.PathScope);
        Assert.Empty(result.Matches);
        await inner.DidNotReceive().SearchCodeAsync(Arg.Any<CodeSearchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchPathsAsync_AfterBudgetExhausted_ReturnsStructuredBudgetBlock()
    {
        var inner = Substitute.For<IReviewContextTools>();
        inner.SearchPathsAsync(Arg.Any<PathSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new PathSearchResult(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    PathSearchModes.Contains,
                    null,
                    [new PathSearchMatch("src/Foo.cs", "csharp", 1)],
                    [],
                    false));

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.SearchPathsToolName],
            1);

        var request = new PathSearchRequest(
            "Foo",
            PathSearchModes.Contains,
            RepositorySearchBranchSides.Source,
            RepositorySearchPathScopes.Repository);

        var first = await sut.SearchPathsAsync(request, CancellationToken.None);
        var second = await sut.SearchPathsAsync(request, CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Success, first.Status);
        Assert.Equal(RepositorySearchStatuses.BlockedBudgetExhausted, second.Status);
        Assert.Equal(BoundedReviewContextTools.SuccessStatus, sut.Attempts[0].Status);
        Assert.Equal(BoundedReviewContextTools.BlockedBudgetExhaustedStatus, sut.Attempts[1].Status);
    }

    [Fact]
    public async Task GetFileNeighborhoodAsync_PathOutsideSeedScope_ReturnsScopeBlock()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetFileNeighborhoodToolName],
            2,
            ["src/Allowed.cs"]);

        var result = await sut.GetFileNeighborhoodAsync("src/Other.cs", RepositorySearchBranchSides.Source, CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.BlockedScopeViolation, result.Status);
        Assert.Equal("src/Other.cs", result.FilePath);
        await inner.DidNotReceive().GetFileNeighborhoodAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchSourceRepoAsync_InnerNoMatchResult_PassesStructuredResultThrough()
    {
        var inner = Substitute.For<IReviewContextTools>();
        inner.SearchSourceRepoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    RepositorySearchStatuses.NoMatch,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    "**/*.cs",
                    [],
                    [],
                    false));

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.SearchSourceRepoToolName],
            1);

        var result = await sut.SearchSourceRepoAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.NoMatch, result.Status);
        Assert.Empty(result.Matches);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task SearchTargetChangedFilesAsync_InnerPartialTruncatedResult_PassesStructuredResultThrough()
    {
        var inner = Substitute.For<IReviewContextTools>();
        inner.SearchTargetChangedFilesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    RepositorySearchStatuses.Partial,
                    RepositorySearchBranchSides.Target,
                    RepositorySearchPathScopes.ChangedFiles,
                    "**/*.cs",
                    [new RepositorySearchMatch("src/Foo.cs", 8, "needle")],
                    [new RepositorySearchLimitation("src/Bar.cs", RepositorySearchLimitationReasons.ResultTruncated, "More matches were omitted.")],
                    true));

        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.SearchTargetChangedFilesToolName],
            1);

        var result = await sut.SearchTargetChangedFilesAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Partial, result.Status);
        Assert.Equal(RepositorySearchBranchSides.Target, result.BranchSide);
        Assert.Equal(RepositorySearchPathScopes.ChangedFiles, result.PathScope);
        Assert.True(result.Truncated);
        Assert.Single(result.Limitations);
        Assert.Equal(RepositorySearchLimitationReasons.ResultTruncated, result.Limitations[0].Reason);
    }

    [Fact]
    public async Task GetLinkedItemDetailsAsync_WhenAllowedAndWithinBudget_DelegatesToInner()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var details = new LinkedItemDetails("42", "Bug", "Broken parser", "body", "Active", new Dictionary<string, string>(), []);
        inner.GetLinkedItemDetailsAsync("42", Arg.Any<CancellationToken>()).Returns(details);
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetLinkedItemDetailsToolName],
            2);

        var result = await sut.GetLinkedItemDetailsAsync("42", CancellationToken.None);

        Assert.Same(details, result);
        var attempt = Assert.Single(sut.Attempts);
        Assert.Equal(BoundedReviewContextTools.SuccessStatus, attempt.Status);
    }

    [Fact]
    public async Task GetLinkedItemDetailsAsync_WhenToolNotAllowed_ReturnsNullWithoutCallingInner()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(inner, [], 2);

        var result = await sut.GetLinkedItemDetailsAsync("42", CancellationToken.None);

        Assert.Null(result);
        await inner.DidNotReceive().GetLinkedItemDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var attempt = Assert.Single(sut.Attempts);
        Assert.Equal(BoundedReviewContextTools.BlockedNotAllowedStatus, attempt.Status);
    }

    [Fact]
    public async Task GetLinkedItemDiscussionAsync_WhenBudgetExhausted_ReturnsEmptyWithoutCallingInner()
    {
        var inner = Substitute.For<IReviewContextTools>();
        var sut = new BoundedReviewContextTools(
            inner,
            [BoundedReviewContextTools.GetLinkedItemDiscussionToolName],
            0);

        var result = await sut.GetLinkedItemDiscussionAsync("42", CancellationToken.None);

        Assert.Empty(result);
        await inner.DidNotReceive().GetLinkedItemDiscussionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var attempt = Assert.Single(sut.Attempts);
        Assert.Equal(BoundedReviewContextTools.BlockedBudgetExhaustedStatus, attempt.Status);
    }
}
