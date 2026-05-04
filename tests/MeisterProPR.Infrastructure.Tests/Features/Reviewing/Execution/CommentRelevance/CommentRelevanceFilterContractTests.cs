// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

public sealed class CommentRelevanceFilterContractTests
{
    [Theory]
    [InlineData("pass-through-v1")]
    [InlineData("heuristic-v1")]
    public async Task Filters_EmitStableRecordedOutputShape(string implementationId)
    {
        var request = CommentRelevanceFilterTestData.CreateRequest(
        [
            CommentRelevanceFilterTestData.CreateComment("Confirmed null dereference in `ExecuteAsync` on line 10.", lineNumber: 10),
            CommentRelevanceFilterTestData.CreateComment("Overall this file could be cleaned up.", CommentSeverity.Suggestion),
        ]);

        ICommentRelevanceFilter filter = implementationId switch
        {
            "pass-through-v1" => new PassThroughCommentRelevanceFilter(),
            "heuristic-v1" => new HeuristicCommentRelevanceFilter(),
            _ => throw new InvalidOperationException("Unknown test filter."),
        };

        var result = await filter.FilterAsync(request, CancellationToken.None);
        var recorded = result.ToRecordedOutput();

        Assert.Equal(implementationId, recorded.ImplementationId);
        Assert.Equal("1.0.0", recorded.ImplementationVersion);
        Assert.Equal("src/Foo.cs", recorded.FilePath);
        Assert.Equal(2, recorded.OriginalCommentCount);
        Assert.Equal(recorded.OriginalCommentCount, recorded.KeptCount + recorded.DiscardedCount);
        Assert.NotNull(recorded.ReasonBuckets);
        Assert.NotNull(recorded.DecisionSources);
        Assert.NotNull(recorded.DegradedComponents);
        Assert.NotNull(recorded.FallbackChecks);
        Assert.NotNull(recorded.Discarded);

        foreach (var discarded in recorded.Discarded)
        {
            Assert.False(string.IsNullOrWhiteSpace(discarded.FilePath));
            Assert.False(string.IsNullOrWhiteSpace(discarded.Severity));
            Assert.False(string.IsNullOrWhiteSpace(discarded.Message));
            Assert.NotEmpty(discarded.ReasonCodes);
            Assert.False(string.IsNullOrWhiteSpace(discarded.DecisionSource));
        }
    }

    [Fact]
    public async Task HeuristicFilter_DiscardsSummaryOnlyCommentWithReasonBucket()
    {
        var filter = new HeuristicCommentRelevanceFilter();
        var request = CommentRelevanceFilterTestData.CreateRequest(
        [
            CommentRelevanceFilterTestData.CreateComment(
                "Overall this file needs more cleanup across multiple places.",
                CommentSeverity.Suggestion),
        ]);

        var result = await filter.FilterAsync(request, CancellationToken.None);
        var recorded = result.ToRecordedOutput();

        Assert.Equal(0, recorded.KeptCount);
        Assert.Equal(1, recorded.DiscardedCount);
        Assert.Equal(1, recorded.ReasonBuckets[CommentRelevanceReasonCodes.SummaryLevelOnly]);
        Assert.Equal(CommentRelevanceReasonCodes.SummaryLevelOnly, recorded.Discarded[0].ReasonCodes[0]);
        Assert.Equal(CommentRelevanceFilterDecision.DeterministicScreeningSource, recorded.Discarded[0].DecisionSource);
    }
}
