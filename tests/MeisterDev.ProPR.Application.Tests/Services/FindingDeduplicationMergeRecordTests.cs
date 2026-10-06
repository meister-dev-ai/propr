// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Checks that both deduplicators report every comment they remove, together with the comment that represents the
///     merged group afterwards, so the synthesis can record each removal in the job protocol.
/// </summary>
public sealed class FindingDeduplicationMergeRecordTests
{
    [Fact]
    public async Task Semantic_WhenTheJudgeMergesTwoComments_ReportsTheKeptAndTheRemovedComment()
    {
        var judge = Substitute.For<IFindingMergeJudge>();
        judge.AreSameDefectClassAsync(Arg.Any<CandidateReviewFinding>(), Arg.Any<CandidateReviewFinding>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var warning = new ReviewComment("src/A.cs", 12, CommentSeverity.Warning, "The cache key ignores the tenant.") { OriginPassKind = "Baseline" };
        var error = new ReviewComment("src/A.cs", 14, CommentSeverity.Error, "Tenants share cache entries because the key has no tenant id.")
        {
            OriginPassKind = "MultiPassUnion",
            OriginPassIndex = 2,
            OriginPassLens = "inventory",
        };
        var unrelated = new ReviewComment("src/B.cs", 3, CommentSeverity.Suggestion, "Rename the helper so its purpose is clear.");

        var result = await new SemanticFindingDeduplicator(judge).DeduplicateAsync([warning, error, unrelated], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([error, unrelated], result.Comments);
        var merge = Assert.Single(result.Merges);
        Assert.Equal(FindingDeduplicationResult.SemanticSameDefectReason, merge.Reason);
        Assert.Same(error, Assert.Single(merge.Kept));
        Assert.Same(warning, Assert.Single(merge.Removed));
    }

    [Fact]
    public async Task Semantic_WhenTheJudgeKeepsBoth_ReportsNoMerge()
    {
        var judge = Substitute.For<IFindingMergeJudge>();
        judge.AreSameDefectClassAsync(Arg.Any<CandidateReviewFinding>(), Arg.Any<CandidateReviewFinding>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var first = new ReviewComment("src/A.cs", 12, CommentSeverity.Warning, "The cache key ignores the tenant.");
        var second = new ReviewComment("src/A.cs", 13, CommentSeverity.Warning, "The retry loop never sleeps between attempts.");

        var result = await new SemanticFindingDeduplicator(judge).DeduplicateAsync([first, second], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([first, second], result.Comments);
        Assert.Empty(result.Merges);
    }

    [Fact]
    public async Task TokenSimilarity_ReportsOneRecordPerMergeGroup_WithItsSurvivingComment()
    {
        var nullFirst = new ReviewComment("src/A.cs", 10, CommentSeverity.Warning, "Null reference risk when the config is missing at startup.");
        var nullSecond = new ReviewComment("src/A.cs", 11, CommentSeverity.Warning, "Null reference risk when the config is missing at startup.");
        var retryFirst = new ReviewComment("src/A.cs", 40, CommentSeverity.Suggestion, "The retry loop never waits between attempts against the server.");
        var retrySecond = new ReviewComment("src/A.cs", 42, CommentSeverity.Suggestion, "The retry loop never waits between attempts against the server.");
        var redirectB = new ReviewComment("src/B.cs", 20, CommentSeverity.Error, "Unvalidated redirect target taken directly from the request query state.");
        var redirectC = new ReviewComment("src/C.cs", 30, CommentSeverity.Error, "Unvalidated redirect target taken directly from the request query state.");

        var result = await new TokenJaccardFindingDeduplicator().DeduplicateAsync(
            [nullFirst, nullSecond, retryFirst, retrySecond, redirectB, redirectC], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(3, result.Merges.Count);
        Assert.All(result.Merges, merge => Assert.Equal(FindingDeduplicationResult.TokenSimilarityReason, merge.Reason));
        var nullMerge = Assert.Single(result.Merges, merge => merge.Removed.Contains(nullSecond));
        Assert.Same(nullFirst, Assert.Single(nullMerge.Kept));
        var retryMerge = Assert.Single(result.Merges, merge => merge.Removed.Contains(retrySecond));
        Assert.Same(retryFirst, Assert.Single(retryMerge.Kept));
        var crossFile = Assert.Single(result.Merges, merge => merge.Removed.Contains(redirectB));
        Assert.Contains(redirectC, crossFile.Removed);
        var consolidated = Assert.Single(crossFile.Kept);
        Assert.Contains(consolidated, result.Comments);
        Assert.All(result.Merges, merge => Assert.All(merge.Kept, kept => Assert.Contains(kept, result.Comments)));
    }
}
