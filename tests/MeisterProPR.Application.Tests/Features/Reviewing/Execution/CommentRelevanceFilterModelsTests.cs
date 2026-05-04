// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class CommentRelevanceFilterModelsTests
{
    [Fact]
    public void CommentRelevanceFilterDecision_DiscardWithoutReasonCodes_Throws()
    {
        var comment = new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Unsupported claim.");

        var exception = Assert.Throws<ArgumentException>(() =>
            new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.DiscardDecision,
                comment,
                [],
                CommentRelevanceFilterDecision.DeterministicScreeningSource));

        Assert.Contains("Discard decisions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentRelevanceFilterResult_BuildsStableCountsAndRecordedOutput()
    {
        var firstComment = new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed issue.");
        var secondComment = new ReviewComment("src/Foo.cs", 24, CommentSeverity.Suggestion, "General cleanup suggestion.");
        var result = new CommentRelevanceFilterResult(
            "heuristic-v1",
            "1.0.0",
            "src/Foo.cs",
            2,
            [
                new CommentRelevanceFilterDecision(
                    CommentRelevanceFilterDecision.KeepDecision,
                    firstComment,
                    [],
                    CommentRelevanceFilterDecision.DeterministicScreeningSource),
                new CommentRelevanceFilterDecision(
                    CommentRelevanceFilterDecision.DiscardDecision,
                    secondComment,
                    [CommentRelevanceReasonCodes.SummaryLevelOnly],
                    CommentRelevanceFilterDecision.DeterministicScreeningSource),
            ],
            ["comment_relevance_evaluator"],
            ["ambiguous_survivors_left_unchanged"],
            "Evaluator unavailable.");

        var recorded = result.ToRecordedOutput();

        Assert.Equal(2, result.OriginalCommentCount);
        Assert.Equal(1, result.KeptCount);
        Assert.Equal(1, result.DiscardedCount);
        Assert.Equal(1, result.ReasonBuckets[CommentRelevanceReasonCodes.SummaryLevelOnly]);
        Assert.Equal(2, recorded.DecisionSources[CommentRelevanceFilterDecision.DeterministicScreeningSource]);
        Assert.Single(recorded.Discarded);
        Assert.Equal("heuristic-v1", recorded.ImplementationId);
        Assert.Equal("src/Foo.cs", recorded.Discarded[0].FilePath);
        Assert.Equal("suggestion", recorded.Discarded[0].Severity);
        Assert.Equal(CommentRelevanceReasonCodes.SummaryLevelOnly, recorded.Discarded[0].ReasonCodes[0]);
    }

    [Fact]
    public void CommentRelevanceFilterSelection_NoneRepresentsBaselineBehavior()
    {
        Assert.False(CommentRelevanceFilterSelection.None.HasSelection);
        Assert.Null(CommentRelevanceFilterSelection.None.SelectedImplementationId);
    }

    [Fact]
    public void CommentRelevanceFilterRequest_RequiresFilePath()
    {
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+line");
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            [file]);

        var exception = Assert.Throws<ArgumentException>(() =>
            new CommentRelevanceFilterRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "heuristic-v1",
                string.Empty,
                file,
                pullRequest,
                [],
                new ReviewSystemContext(null, [], null),
                null));

        Assert.Contains("File path", exception.Message, StringComparison.Ordinal);
    }
}
