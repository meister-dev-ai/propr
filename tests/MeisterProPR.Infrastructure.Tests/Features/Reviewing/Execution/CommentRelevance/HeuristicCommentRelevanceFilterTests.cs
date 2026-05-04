// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

public sealed class HeuristicCommentRelevanceFilterTests
{
    [Theory]
    [InlineData(CommentSeverity.Warning, "This likely fails when configuration is missing.", CommentRelevanceReasonCodes.HedgingLanguage)]
    [InlineData(CommentSeverity.Suggestion, "Consider refactoring this file overall.", CommentRelevanceReasonCodes.NonActionableSuggestion)]
    [InlineData(CommentSeverity.Warning, "Overall this file has too many responsibilities.", CommentRelevanceReasonCodes.SummaryLevelOnly)]
    [InlineData(
        CommentSeverity.Warning,
        "The tool output was truncated so this might be a defect.",
        CommentRelevanceReasonCodes.ToolingLimitationMisclassified)]
    [InlineData(CommentSeverity.Warning, "Another file likely initializes this differently.", CommentRelevanceReasonCodes.UnverifiableCrossFileClaim)]
    [InlineData(CommentSeverity.Warning, "behavior is broken in some cases.", CommentRelevanceReasonCodes.MissingConcreteObservable)]
    public async Task FilterAsync_AssignsExpectedDiscardReasonCodes(CommentSeverity severity, string message, string expectedReasonCode)
    {
        var filter = new HeuristicCommentRelevanceFilter();
        var request = CommentRelevanceFilterTestData.CreateRequest([CommentRelevanceFilterTestData.CreateComment(message, severity)]);

        var result = await filter.FilterAsync(request, CancellationToken.None);

        Assert.Equal(0, result.KeptCount);
        Assert.Equal(1, result.DiscardedCount);
        Assert.Equal(1, result.ReasonBuckets[expectedReasonCode]);
    }

    [Fact]
    public async Task FilterAsync_DetectsSeverityOverstatedWithoutChangingSeverity()
    {
        var filter = new HeuristicCommentRelevanceFilter();
        var comment = CommentRelevanceFilterTestData.CreateComment(
            "Critical failure likely exists somewhere else in another file.",
            CommentSeverity.Error);

        var result = await filter.FilterAsync(CommentRelevanceFilterTestData.CreateRequest([comment]), CancellationToken.None);
        var discarded = result.ToRecordedOutput().Discarded.Single();

        Assert.Contains(CommentRelevanceReasonCodes.SeverityOverstated, discarded.ReasonCodes);
        Assert.Equal("error", discarded.Severity);
    }

    [Fact]
    public async Task FilterAsync_DiscardsWrongFileOrAnchor_WhenCommentTargetsDifferentFile()
    {
        var filter = new HeuristicCommentRelevanceFilter();
        var request = CommentRelevanceFilterTestData.CreateRequest(
            [CommentRelevanceFilterTestData.CreateComment("Confirmed issue in another file.", lineNumber: 99, filePath: "src/Other.cs")]);

        var result = await filter.FilterAsync(request, CancellationToken.None);

        Assert.Equal(1, result.ReasonBuckets[CommentRelevanceReasonCodes.WrongFileOrAnchor]);
    }

    [Fact]
    public async Task FilterAsync_PrunesDuplicateLocalPattern_KeepingStrongerComment()
    {
        var filter = new HeuristicCommentRelevanceFilter();
        var stronger = CommentRelevanceFilterTestData.CreateComment(
            "Null dereference in ExecuteAsync when request is null before validation step.",
            CommentSeverity.Error,
            2);
        var weaker = CommentRelevanceFilterTestData.CreateComment(
            "Null dereference in ExecuteAsync when request is null before validation step.",
            CommentSeverity.Warning,
            3);

        var result = await filter.FilterAsync(CommentRelevanceFilterTestData.CreateRequest([stronger, weaker]), CancellationToken.None);
        var recorded = result.ToRecordedOutput();

        Assert.Equal(1, recorded.KeptCount);
        Assert.Equal(1, recorded.DiscardedCount);
        Assert.Equal(1, recorded.ReasonBuckets[CommentRelevanceReasonCodes.DuplicateLocalPattern]);
        Assert.Equal(3, recorded.Discarded[0].LineNumber);
        Assert.Equal("warning", recorded.Discarded[0].Severity);
    }
}
