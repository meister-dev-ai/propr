// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

public sealed class HeuristicCommentRelevanceFilterTests
{
    [Fact]
    public async Task FilterAsync_DiscardsMissingConcreteObservable_WhenNoLineOrCodeToken()
    {
        var filter = new HeuristicCommentRelevanceFilter();
        var request = CommentRelevanceFilterTestData.CreateRequest([CommentRelevanceFilterTestData.CreateComment("behavior is broken in some cases.")]);

        var result = await filter.FilterAsync(request, CancellationToken.None);

        Assert.Equal(0, result.KeptCount);
        Assert.Equal(1, result.DiscardedCount);
        Assert.Equal(1, result.ReasonBuckets[CommentRelevanceReasonCodes.MissingConcreteObservable]);
    }

    [Theory]
    [InlineData("This likely fails when configuration is missing on line 5.")]
    [InlineData("Consider refactoring GetById overall.")]
    [InlineData("The tool output was truncated so this might be a defect at line 9.")]
    [InlineData("Critical failure probably exists here in this method.")]
    public async Task FilterAsync_KeepsHedgeVagueOrSeverityLanguage_WhenConcreteAndCorrectlyAnchored(string message)
    {
        // Text-shaped hedge/vague/tooling/severity screening moved to the embedding-based semantic comment
        // screener; the heuristic relevance filter no longer discards on those phrases. A concrete,
        // correctly-anchored comment therefore survives regardless of such wording.
        var filter = new HeuristicCommentRelevanceFilter();
        var request = CommentRelevanceFilterTestData.CreateRequest([CommentRelevanceFilterTestData.CreateComment(message, CommentSeverity.Warning, 3)]);

        var result = await filter.FilterAsync(request, CancellationToken.None);

        Assert.Equal(1, result.KeptCount);
        Assert.Equal(0, result.DiscardedCount);
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

    [Fact]
    public async Task FilterAsync_MemberAccessTokens_AreNotCountedAsCrossFileReferences()
    {
        // Member-access expressions (job.ClientId, job.IterationId) must not be miscounted as file
        // references. This single-file finding names its own file once and is otherwise concrete, so it
        // survives screening instead of being discarded as an unverifiable cross-file claim.
        var filter = new HeuristicCommentRelevanceFilter();
        var comment = CommentRelevanceFilterTestData.CreateComment(
            "GetFileDiffHandler.cs resolves the stored diff with job.ClientId, job.RepositoryId and "
            + "job.PullRequestId but passes a null revision key, so job.IterationId is ignored.",
            CommentSeverity.Warning,
            2);

        var result = await filter.FilterAsync(CommentRelevanceFilterTestData.CreateRequest([comment]), CancellationToken.None);

        Assert.Equal(1, result.KeptCount);
        Assert.Equal(0, result.DiscardedCount);
    }

    [Fact]
    public async Task FilterAsync_TwoDistinctFileReferences_AreFlaggedAsCrossFileClaim()
    {
        var filter = new HeuristicCommentRelevanceFilter();
        var comment = CommentRelevanceFilterTestData.CreateComment(
            "The value written in ReviewArchiveStore.cs is read back differently in GetFileDiffHandler.cs.",
            CommentSeverity.Warning,
            2);

        var result = await filter.FilterAsync(CommentRelevanceFilterTestData.CreateRequest([comment]), CancellationToken.None);

        Assert.Equal(1, result.ReasonBuckets[CommentRelevanceReasonCodes.UnverifiableCrossFileClaim]);
    }

    [Fact]
    public async Task FilterAsync_SameFileReferencedTwice_IsNotFlaggedAsCrossFileClaim()
    {
        // A finding that names its own single file more than once is not a cross-file claim.
        var filter = new HeuristicCommentRelevanceFilter();
        var comment = CommentRelevanceFilterTestData.CreateComment(
            "GetFileDiffHandler.cs passes a null revision key; GetFileDiffHandler.cs should instead scope "
            + "the stored lookup to the job revision.",
            CommentSeverity.Warning,
            2);

        var result = await filter.FilterAsync(CommentRelevanceFilterTestData.CreateRequest([comment]), CancellationToken.None);

        Assert.Equal(1, result.KeptCount);
    }

    [Fact]
    public async Task FilterAsync_LeadingSlashPathDifference_IsNotFlaggedWrongFileOrAnchor()
    {
        // The finding anchors to "src/Foo.cs" while the file under review is recorded as "/src/Foo.cs".
        // The leading-slash difference denotes the same file and must not be a wrong-file discard.
        var filter = new HeuristicCommentRelevanceFilter();
        var comment = CommentRelevanceFilterTestData.CreateComment(
            "Concrete confirmed defect on this line of the stored lookup.",
            CommentSeverity.Error,
            3);
        var request = CommentRelevanceFilterTestData.CreateRequest([comment], filePath: "/src/Foo.cs");

        var result = await filter.FilterAsync(request, CancellationToken.None);

        Assert.Equal(1, result.KeptCount);
        Assert.Equal(0, result.DiscardedCount);
    }

    [Fact]
    public async Task FilterAsync_MemberAccessFindingWithLeadingSlashAnchor_Survives()
    {
        // The real regression: a member-access-dense finding anchored to "src/...GetFileDiffHandler.cs" on a
        // file recorded as "/src/...GetFileDiffHandler.cs" previously drew both unverifiable_cross_file_claim
        // (dotted tokens) and wrong_file_or_anchor (leading slash) and was hard-dropped. It now survives.
        var filter = new HeuristicCommentRelevanceFilter();
        const string path =
            "src/MeisterProPR.Application/Features/Reviewing/Diagnostics/Queries/GetFileDiff/GetFileDiffHandler.cs";
        var comment = CommentRelevanceFilterTestData.CreateComment(
            "TryGetStoredFileDiffAsync calls GetFileDiffAsync with job.ClientId, job.RepositoryId and "
            + "job.PullRequestId but a null revision key, so job.IterationId is dropped and the stored lookup "
            + "returns a stale diff for the wrong iteration.",
            CommentSeverity.Error,
            143,
            path);
        var request = CommentRelevanceFilterTestData.CreateRequest([comment], filePath: "/" + path, lineCount: 205);

        var result = await filter.FilterAsync(request, CancellationToken.None);

        Assert.Equal(1, result.KeptCount);
        Assert.Equal(0, result.DiscardedCount);
    }
}
