// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Tests for AdoCommentPoster mapping logic.
///     Since GitHttpClient is sealed, these tests verify domain model behaviour
///     and the comment construction logic without making real ADO API calls.
/// </summary>
public class AdoCommentPosterTests
{
    [Theory]
    [InlineData(CommentSeverity.Error)]
    [InlineData(CommentSeverity.Warning)]
    [InlineData(CommentSeverity.Suggestion)]
    [InlineData(CommentSeverity.Info)]
    public void ReviewComment_AllSeveritiesSupported(CommentSeverity severity)
    {
        var comment = new ReviewComment("/file.cs", null, severity, "Message.");
        Assert.Equal(severity, comment.Severity);
    }

    [Fact]
    public void ReviewComment_WithLineNumber_SupportsInlineComment()
    {
        var comment = new ReviewComment("/src/Program.cs", 42, CommentSeverity.Error, "Null ref here.");
        Assert.True(comment.LineNumber.HasValue);
        Assert.Equal(42, comment.LineNumber);
    }

    [Fact]
    public void ReviewComment_WithoutLineNumber_SupportFileAnchor()
    {
        var comment = new ReviewComment("/src/Program.cs", null, CommentSeverity.Info, "File-level note.");
        Assert.NotNull(comment.FilePath);
        Assert.Null(comment.LineNumber);
    }

    [Fact]
    public void ReviewResult_EmptyComments_HasEmptyList()
    {
        var result = new ReviewResult("Summary only.", new List<ReviewComment>().AsReadOnly());
        Assert.Empty(result.Comments);
    }

    [Fact]
    public void ReviewResult_MultipleComments_OrderPreserved()
    {
        var comments = new List<ReviewComment>
        {
            new("/file1.cs", 1, CommentSeverity.Error, "First"),
            new("/file2.cs", 2, CommentSeverity.Warning, "Second"),
            new(null, null, CommentSeverity.Info, "Third"),
        }.AsReadOnly();

        var result = new ReviewResult("Summary", comments);
        Assert.Equal(3, result.Comments.Count);
        Assert.Equal("/file1.cs", result.Comments[0].FilePath);
        Assert.Equal("/file2.cs", result.Comments[1].FilePath);
        Assert.Null(result.Comments[2].FilePath);
    }

    [Fact]
    public void ReviewResult_SummaryIsPresent()
    {
        var result = new ReviewResult("This is the AI summary.", new List<ReviewComment>().AsReadOnly());
        Assert.Equal("This is the AI summary.", result.Summary);
    }

    [Fact]
    public void ReviewResult_WithFileLevelComment_HasFilePath()
    {
        var comment = new ReviewComment("/src/MyFile.cs", 10, CommentSeverity.Warning, "Issue here.");
        Assert.NotNull(comment.FilePath);
        Assert.Equal(10, comment.LineNumber);
    }

    [Fact]
    public void ReviewResult_WithPrLevelComment_HasNullFilePath()
    {
        var comment = new ReviewComment(null, null, CommentSeverity.Info, "PR-level comment.");
        Assert.Null(comment.FilePath);
        Assert.Null(comment.LineNumber);
    }

    [Fact]
    public void ReviewComment_WithLineNumberZero_IsNotValidInlinePosition()
    {
        // ADO requires line numbers >= 1; LineNumber = 0 must be treated as
        // "no line anchor" so CommentPosition is not constructed with Line=0.
        var comment = new ReviewComment("/src/Program.cs", 0, CommentSeverity.Warning, "Bad line.");
        // The condition that gates CommentPosition construction must exclude 0.
        Assert.False(comment.LineNumber.HasValue && comment.LineNumber.Value > 0);
    }

    [Fact]
    public void ResolveAnchorContext_UsesCompareIterationForIncrementalInlineAnchor()
    {
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");
        var changeTrackingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["/src/Program.cs"] = 177,
        };

        var anchor = AdoCommentPoster.ResolveAnchorContext(
            comment,
            7,
            3,
            changeTrackingIds);

        Assert.Equal(PublicationAnchorPrecision.Inline, anchor.AnchorPrecision);
        Assert.Equal("/src/Program.cs", anchor.NormalizedFilePath);
        Assert.Equal(42, anchor.ResolvedLineNumber);
        Assert.Equal("177", anchor.ProviderTrackingReference);
        Assert.Equal("3:7", anchor.CompareRevisionReference);
    }

    [Fact]
    public void ResolveAnchorContext_WithoutCompareIteration_PinsFullDiffCompareReference()
    {
        // A full (non-incremental) review has no compare iteration. The anchor must still pin
        // the reviewed iteration as the right side of the full-diff view (iteration 1 → N);
        // otherwise Azure DevOps resolves the line numbers against the latest iteration at
        // posting time and every anchor shifts when the PR advanced mid-review.
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");
        var changeTrackingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["/src/Program.cs"] = 177,
        };

        var anchor = AdoCommentPoster.ResolveAnchorContext(
            comment,
            7,
            null,
            changeTrackingIds);

        Assert.Equal(PublicationAnchorPrecision.Inline, anchor.AnchorPrecision);
        Assert.Equal("/src/Program.cs", anchor.NormalizedFilePath);
        Assert.Equal(42, anchor.ResolvedLineNumber);
        Assert.Equal("177", anchor.ProviderTrackingReference);
        Assert.Equal("1:7", anchor.CompareRevisionReference);
    }

    [Fact]
    public void ResolveAnchorContext_SingleIterationFullReview_PinsFullDiffCompareReference()
    {
        // The most common case: a pull request with exactly one iteration. The full-diff view
        // is iteration 1 compared with itself, which is also what the Azure DevOps web UI sends.
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");
        var changeTrackingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["/src/Program.cs"] = 177,
        };

        var anchor = AdoCommentPoster.ResolveAnchorContext(comment, 1, null, changeTrackingIds);

        Assert.Equal("1:1", anchor.CompareRevisionReference);
    }

    [Fact]
    public void ResolveAnchorContext_NonPositiveCompareIteration_PinsFullDiffCompareReference()
    {
        // A non-positive compare iteration is not a usable incremental baseline, so the anchor
        // pins the full-diff view instead of posting unpinned.
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");
        var changeTrackingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["/src/Program.cs"] = 177,
        };

        var anchor = AdoCommentPoster.ResolveAnchorContext(comment, 7, 0, changeTrackingIds);

        Assert.Equal("1:7", anchor.CompareRevisionReference);
    }

    [Fact]
    public void BuildThreadContexts_IterationBeyondShortRange_OmitsIterationContext()
    {
        // The iteration-context fields are shorts; an unrepresentable reviewed iteration must
        // post an unpinned inline thread instead of a wrapped negative iteration pair.
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");
        var changeTrackingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["/src/Program.cs"] = 177,
        };

        var anchor = AdoCommentPoster.ResolveAnchorContext(comment, short.MaxValue + 1, null, changeTrackingIds);
        var (threadContext, prThreadContext) = AdoCommentPoster.BuildThreadContexts(anchor);

        Assert.NotNull(threadContext);
        Assert.Equal(42, threadContext!.RightFileStart!.Line);
        Assert.NotNull(prThreadContext);
        Assert.Null(prThreadContext!.IterationContext);
    }

    [Fact]
    public void ResolveAnchorContext_UnresolvableIteration_OmitsCompareRevisionReference()
    {
        // Without a valid reviewed iteration there is nothing to pin the anchor to.
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");
        var changeTrackingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["/src/Program.cs"] = 177,
        };

        var anchor = AdoCommentPoster.ResolveAnchorContext(
            comment,
            0,
            null,
            changeTrackingIds);

        Assert.Equal(PublicationAnchorPrecision.Inline, anchor.AnchorPrecision);
        Assert.Null(anchor.CompareRevisionReference);
    }

    [Fact]
    public void ResolveAnchorContext_MissingTrackingId_FallsBackToFileAnchor()
    {
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");

        var anchor = AdoCommentPoster.ResolveAnchorContext(
            comment,
            7,
            3,
            new Dictionary<string, int>());

        Assert.Equal(PublicationAnchorPrecision.File, anchor.AnchorPrecision);
        Assert.Equal("/src/Program.cs", anchor.NormalizedFilePath);
        Assert.Null(anchor.ResolvedLineNumber);
        Assert.Null(anchor.ProviderTrackingReference);
        Assert.Equal("3:7", anchor.CompareRevisionReference);
    }

    [Fact]
    public void BuildThreadContexts_InlineAnchor_UsesComparedIterations()
    {
        var anchor = new PublicationAnchorContext(
            "/src/Program.cs",
            42,
            "/src/Program.cs",
            42,
            PublicationAnchorPrecision.Inline,
            "177",
            "3:7");

        var (threadContext, prThreadContext) = AdoCommentPoster.BuildThreadContexts(anchor);

        Assert.NotNull(threadContext);
        Assert.Equal("/src/Program.cs", threadContext!.FilePath);
        Assert.Equal(42, threadContext.RightFileStart!.Line);
        Assert.Equal(42, threadContext.RightFileEnd!.Line);

        Assert.NotNull(prThreadContext);
        Assert.Equal(177, prThreadContext!.ChangeTrackingId);
        Assert.NotNull(prThreadContext.IterationContext);
        Assert.Equal(3, prThreadContext.IterationContext.FirstComparingIteration);
        Assert.Equal(7, prThreadContext.IterationContext.SecondComparingIteration);
    }

    [Fact]
    public void BuildThreadContexts_FullReviewInlineAnchor_PinsReviewedIteration()
    {
        // End-to-end over the anchor helpers: a full review of iteration 7 (no compare
        // iteration) must produce an inline thread whose iteration context pins the reviewed
        // iteration (full-diff view 1 → 7), so the posted line numbers keep meaning "line in
        // the reviewed file" even when the pull request advances before or after posting.
        var comment = new ReviewComment("src/Program.cs", 42, CommentSeverity.Warning, "Bad line.");
        var changeTrackingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["/src/Program.cs"] = 177,
        };

        var anchor = AdoCommentPoster.ResolveAnchorContext(comment, 7, null, changeTrackingIds);
        var (threadContext, prThreadContext) = AdoCommentPoster.BuildThreadContexts(anchor);

        Assert.NotNull(threadContext);
        Assert.Equal("/src/Program.cs", threadContext!.FilePath);
        Assert.Equal(42, threadContext.RightFileStart!.Line);
        Assert.Equal(42, threadContext.RightFileEnd!.Line);

        Assert.NotNull(prThreadContext);
        Assert.Equal(177, prThreadContext!.ChangeTrackingId);
        Assert.NotNull(prThreadContext.IterationContext);
        Assert.Equal(1, prThreadContext.IterationContext.FirstComparingIteration);
        Assert.Equal(7, prThreadContext.IterationContext.SecondComparingIteration);
    }

    [Fact]
    public void BuildThreadContexts_InlineAnchorWithoutCompareRevision_OmitsIterationContext()
    {
        // An anchor context that carries no compare revision reference (unresolvable reviewed
        // iteration) still posts inline, just without an iteration pin.
        var anchor = new PublicationAnchorContext(
            "/src/Program.cs",
            42,
            "/src/Program.cs",
            42,
            PublicationAnchorPrecision.Inline,
            "177");

        var (threadContext, prThreadContext) = AdoCommentPoster.BuildThreadContexts(anchor);

        Assert.NotNull(threadContext);
        Assert.Equal("/src/Program.cs", threadContext!.FilePath);
        Assert.Equal(42, threadContext.RightFileStart!.Line);
        Assert.Equal(42, threadContext.RightFileEnd!.Line);

        Assert.NotNull(prThreadContext);
        Assert.Equal(177, prThreadContext!.ChangeTrackingId);
        Assert.Null(prThreadContext.IterationContext);
    }

    [Fact]
    public void BuildThreadContexts_FileFallback_OmitsPrThreadContext()
    {
        var anchor = new PublicationAnchorContext(
            "/src/Program.cs",
            42,
            "/src/Program.cs",
            null,
            PublicationAnchorPrecision.File,
            CompareRevisionReference: "3:7");

        var (threadContext, prThreadContext) = AdoCommentPoster.BuildThreadContexts(anchor);

        Assert.NotNull(threadContext);
        Assert.Equal("/src/Program.cs", threadContext!.FilePath);
        Assert.Null(threadContext.RightFileStart);
        Assert.Null(threadContext.RightFileEnd);
        Assert.Null(prThreadContext);
    }

    [Fact]
    public void BuildThreadContexts_PrLevelFallback_OmitsAllAnchorMetadata()
    {
        var anchor = new PublicationAnchorContext(
            null,
            42,
            null,
            null,
            PublicationAnchorPrecision.PrLevel,
            CompareRevisionReference: "3:7");

        var (threadContext, prThreadContext) = AdoCommentPoster.BuildThreadContexts(anchor);

        Assert.Null(threadContext);
        Assert.Null(prThreadContext);
    }

    // Content-length truncation tests

    [Fact]
    public void TruncateIfNeeded_ShortMessage_ReturnsUnchanged()
    {
        var message = "Short message.";
        var result = AdoCommentPoster.TruncateIfNeeded(message);
        Assert.Equal(message, result);
    }

    [Fact]
    public void TruncateIfNeeded_ExactlyAtLimit_ReturnsUnchanged()
    {
        var message = new string('x', AdoCommentPoster.MaxCommentLength);
        var result = AdoCommentPoster.TruncateIfNeeded(message);
        Assert.Equal(message, result);
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_TruncatesAndAppendsNotice()
    {
        var message = new string('a', AdoCommentPoster.MaxCommentLength + 5_000);
        var result = AdoCommentPoster.TruncateIfNeeded(message);
        Assert.True(result.Length <= AdoCommentPoster.MaxCommentLength);
        Assert.Contains("truncated", result);
        Assert.Contains("admin UI", result);
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_TruncatesAtWordBoundary()
    {
        // Build a message where the cutoff lands in the middle of "boundary"
        var prefix = new string('x', AdoCommentPoster.MaxCommentLength - 10) + " boundary extra";
        var result = AdoCommentPoster.TruncateIfNeeded(prefix);
        Assert.True(result.Length <= AdoCommentPoster.MaxCommentLength);
        // Result should not start mid-word from the overflow
        Assert.DoesNotContain("boundary", result.Split('\n')[0]);
    }

    [Fact]
    public void BuildSummaryText_QuotedShellVariables_RemainReadable()
    {
        var result = new ReviewResult(
            "Run dotnet \"$ProCursorDll\" --output \"$ApiDll\" to validate the fix.",
            []);

        var summary = AdoCommentPoster.BuildSummaryText(result);

        Assert.Contains("dotnet \"$ProCursorDll\" --output \"$ApiDll\"", summary);
        Assert.DoesNotContain("&quot;", summary);
    }

    [Fact]
    public void BuildSummaryText_MarksContextDegradedAndSkippedFiles()
    {
        var result = new ReviewResult("Overall summary.", [])
        {
            ContextDegradedFilePaths = ["src/BigService.cs"],
            ContextSkippedFilePaths = ["src/Generated/Huge.g.cs"],
        };

        var summary = AdoCommentPoster.BuildSummaryText(result);

        Assert.Contains("Reviewed diff-only", summary);
        Assert.Contains("src/BigService.cs", summary);
        Assert.Contains("Skipped — exceeds model context window", summary);
        Assert.Contains("src/Generated/Huge.g.cs", summary);
    }

    [Fact]
    public void BuildSummaryText_OmitsContextBudgetSectionsWhenNoneApply()
    {
        var result = new ReviewResult("Overall summary.", []);

        var summary = AdoCommentPoster.BuildSummaryText(result);

        Assert.DoesNotContain("Reviewed diff-only", summary);
        Assert.DoesNotContain("Skipped — exceeds model context window", summary);
    }

    [Theory]
    [InlineData(CommentSeverity.Error, "ERROR")]
    [InlineData(CommentSeverity.Warning, "WARNING")]
    public void FormatInlineCommentBody_UnsafeMarkup_IsNeutralizedWithoutEncodingQuotes(
        CommentSeverity severity,
        string expectedPrefix)
    {
        var comment = new ReviewComment(
            "/src/Foo.cs",
            7,
            severity,
            "Use \"$ApiDll\" after removing <script>alert('xss')</script>.");

        var body = AdoCommentPoster.FormatInlineCommentBody(comment);

        Assert.StartsWith($"{expectedPrefix}: ", body, StringComparison.Ordinal);
        Assert.Contains("\"$ApiDll\"", body);
        Assert.DoesNotContain("&quot;", body);
        Assert.Equal(-1, body.IndexOf("<script>", StringComparison.Ordinal));
        Assert.Contains("<\u200Bscript>", body);
    }

    [Fact]
    public void CaptureCreatedComments_WithCreatedComment_CapturesCommentAndThreadIds()
    {
        var createdThread = new GitPullRequestCommentThread
        {
            Id = 5150,
            Comments = [new Comment { Id = 7, Content = "WARNING: Guard this null case." }],
        };

        var captured = AdoCommentPoster.CaptureCreatedComments(createdThread, "/src/Program.cs", 42);

        var reference = Assert.Single(captured);
        Assert.Equal("7", reference.ProviderCommentId);
        Assert.Equal("5150", reference.ProviderThreadId);
        Assert.Equal("/src/Program.cs", reference.FilePath);
        Assert.Equal(42, reference.Line);
    }

    [Fact]
    public void CaptureCreatedComments_WithoutComments_ReturnsEmpty()
    {
        var createdThread = new GitPullRequestCommentThread { Id = 5150 };

        var captured = AdoCommentPoster.CaptureCreatedComments(createdThread, "/src/Program.cs", 42);

        Assert.Empty(captured);
    }

    [Fact]
    public void CaptureCreatedComments_WithNullThread_ReturnsEmpty()
    {
        var captured = AdoCommentPoster.CaptureCreatedComments(null, "/src/Program.cs", 42);

        Assert.Empty(captured);
    }

    [Fact]
    public void CaptureCreatedComments_DropsCommentsWithoutResolvableId()
    {
        var createdThread = new GitPullRequestCommentThread
        {
            Id = 5150,
            Comments =
            [
                new Comment { Id = 0, Content = "Unidentified comment" },
                new Comment { Id = 9, Content = "WARNING: Guard this null case." },
            ],
        };

        var captured = AdoCommentPoster.CaptureCreatedComments(createdThread, "/src/Program.cs", 42);

        var reference = Assert.Single(captured);
        Assert.Equal("9", reference.ProviderCommentId);
    }

    // Per-thread posting-failure isolation. These exercise the extracted posting loop through the
    // thread-creation seam, so one provider rejection cannot abort the rest of the pass.

    private static readonly Guid PosterBotId = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task PostResolvedThreadsAsync_OneInlineOfThreeRejected_StillPostsTheOthers()
    {
        var invocations = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            invocations.Add(message);
            return message.Contains("REJECT", StringComparison.Ordinal)
                ? throw new InvalidOperationException("TF401027: the thread was rejected by the provider.")
                : Task.FromResult(CreatedThread(invocations.Count, message));
        };

        var result = new ReviewResult(
            "All clear.",
            new List<ReviewComment>
            {
                new("/src/A.cs", 1, CommentSeverity.Error, "first issue"),
                new("/src/B.cs", 2, CommentSeverity.Warning, "REJECT this one"),
                new("/src/C.cs", 3, CommentSeverity.Suggestion, "third issue"),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, factory);

        // Summary + all three inline comments were attempted — the loop did not abort at the failure.
        Assert.Equal(4, invocations.Count);
        Assert.Equal(2, diagnostics.PostedCount);
        Assert.Equal(1, diagnostics.FailedCount);
        var failure = Assert.Single(diagnostics.PostingFailures);
        Assert.Equal("inline", failure.ThreadKind);
        Assert.Equal("/src/B.cs", failure.FilePath);
        Assert.Equal(2, failure.Line);
        Assert.Contains("TF401027", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_SummaryRejected_StillPostsInlineThreads()
    {
        var invocations = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            invocations.Add(message);
            return message.StartsWith("**AI Review Summary**", StringComparison.Ordinal)
                ? throw new InvalidOperationException("TF401019: the summary thread was rejected.")
                : Task.FromResult(CreatedThread(invocations.Count, message));
        };

        var result = new ReviewResult(
            "Summary body.",
            new List<ReviewComment>
            {
                new("/src/A.cs", 1, CommentSeverity.Error, "inline still posts"),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, factory);

        Assert.Equal(2, invocations.Count);
        Assert.Equal(1, diagnostics.PostedCount);
        var failure = Assert.Single(diagnostics.PostingFailures);
        Assert.Equal("summary", failure.ThreadKind);
        Assert.Null(failure.FilePath);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_AllThreadsRejected_ThrowsPublicationFailure()
    {
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
            throw new InvalidOperationException("TF401: rejected " + message);

        var result = new ReviewResult(
            "Summary body.",
            new List<ReviewComment>
            {
                new("/src/A.cs", 1, CommentSeverity.Error, "one"),
                new("/src/B.cs", 2, CommentSeverity.Warning, "two"),
            }.AsReadOnly());

        var exception = await Assert.ThrowsAsync<ReviewCommentPublicationFailedException>(() => PostAsync(result, factory));

        Assert.Equal(0, exception.Diagnostics.PostedCount);
        Assert.Equal(3, exception.Diagnostics.FailedCount);
        Assert.Equal(3, exception.InnerExceptions.Count);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_CallerCancellation_PropagatesInsteadOfBeingSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        AdoCommentPoster.AdoThreadFactory factory = (_, _, _, token) => throw new OperationCanceledException(token);

        var result = new ReviewResult(
            "Summary body.",
            new List<ReviewComment>
            {
                new("/src/A.cs", 1, CommentSeverity.Error, "one"),
            }.AsReadOnly());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostAsync(result, factory, cts.Token));
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_InlineTimeout_IsIsolatedNotPropagated()
    {
        // A provider request timeout surfaces as TaskCanceledException (an OperationCanceledException) even
        // though the caller never cancelled — it must be isolated like any other rejection, not abort the pass.
        var invocations = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            invocations.Add(message);
            return message.Contains("TIMEOUT", StringComparison.Ordinal)
                ? throw new TaskCanceledException("The request timed out.")
                : Task.FromResult(CreatedThread(invocations.Count, message));
        };

        var result = new ReviewResult(
            "All clear.",
            new List<ReviewComment>
            {
                new("/src/A.cs", 1, CommentSeverity.Error, "first issue"),
                new("/src/B.cs", 2, CommentSeverity.Warning, "TIMEOUT here"),
                new("/src/C.cs", 3, CommentSeverity.Suggestion, "third issue"),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, factory);

        Assert.Equal(4, invocations.Count);
        Assert.Equal(2, diagnostics.PostedCount);
        var failure = Assert.Single(diagnostics.PostingFailures);
        Assert.Equal("/src/B.cs", failure.FilePath);
    }

    private static Task<ReviewCommentPostingDiagnosticsDto> PostAsync(
        ReviewResult result,
        AdoCommentPoster.AdoThreadFactory factory,
        CancellationToken cancellationToken = default)
    {
        var poster = new AdoCommentPoster(null!, null!);
        return poster.PostResolvedThreadsAsync(
            result,
            factory,
            PosterBotId,
            clientId: null,
            repositoryId: "repo",
            pullRequestId: 1,
            iterationId: 1,
            compareToIterationId: null,
            changeTrackingIds: new Dictionary<string, int>(),
            existingThreads: null,
            publicationIdentity: null,
            cancellationToken);
    }

    private static GitPullRequestCommentThread CreatedThread(int id, string message)
    {
        return new GitPullRequestCommentThread
        {
            Id = id,
            Comments = [new Comment { Id = (short)id, Content = message }],
        };
    }
}
