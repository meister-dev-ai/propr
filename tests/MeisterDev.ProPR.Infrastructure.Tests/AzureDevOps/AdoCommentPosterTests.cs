// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Tests for AdoCommentPoster mapping logic.
///     Since GitHttpClient is sealed, these tests verify domain model behaviour
///     and the comment construction logic without making real ADO API calls.
/// </summary>
public class AdoCommentPosterTests
{
    /// <summary>The Azure DevOps organization these fixtures post into.</summary>
    private const string Host = "https://dev.azure.com/org";

    private const string Project = "project";

    /// <summary>The marker every summary the poster writes starts with.</summary>
    private const string SummaryPrefix = "**AI Review Summary**";

    private const string Refusal = "Review not started: 312 changed files exceed the limit of 150.";

    private const string OversizedRepository = "Review not started: the repository is 4,096 MB.";

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
        var result = AdoCommentPoster.TruncateIfNeeded(message, TestPostedCommentComposer.Default);
        Assert.Equal(message, result);
    }

    [Fact]
    public void TruncateIfNeeded_ExactlyAtLimit_ReturnsUnchanged()
    {
        var message = new string('x', AdoCommentPoster.MaxCommentLength);
        var result = AdoCommentPoster.TruncateIfNeeded(message, TestPostedCommentComposer.Default);
        Assert.Equal(message, result);
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_TruncatesAndAppendsNotice()
    {
        var message = new string('a', AdoCommentPoster.MaxCommentLength + 5_000);
        var result = AdoCommentPoster.TruncateIfNeeded(message, TestPostedCommentComposer.Default);
        Assert.True(result.Length <= AdoCommentPoster.MaxCommentLength);
        Assert.Contains("truncated", result);
        Assert.Contains("admin UI", result);
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_TruncatesAtWordBoundary()
    {
        // Build a message where the cutoff lands in the middle of "boundary"
        var prefix = new string('x', AdoCommentPoster.MaxCommentLength - 10) + " boundary extra";
        var result = AdoCommentPoster.TruncateIfNeeded(prefix, TestPostedCommentComposer.Default);
        Assert.True(result.Length <= AdoCommentPoster.MaxCommentLength);
        // Result should not start mid-word from the overflow
        Assert.DoesNotContain("boundary", result.Split('\n')[0]);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_PreservesNarrativeAndPublishesActionsAndOfferedFindingTotals()
    {
        var messages = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            messages.Add(message);
            return Task.FromResult(CreatedThread(messages.Count, message));
        };
        var risk = new FindingRiskAssessment(
            FindingImpact.Moderate, FindingLikelihood.Possible,
            "A request fails.", "Requires the enabled option.", "review_model", FindingRiskAssessment.CurrentRubricVersion,
            FindingConfidence.High, "The failing branch is visible.");
        var result = new ReviewResult(
            "Preserve this conclusion and its withholding note.",
            [
                new ReviewComment("src/A.cs", 7, CommentSeverity.Warning, "Guard the request.") { RiskAssessment = risk },
                new ReviewComment(null, null, CommentSeverity.Info, "Check the shared setting."),
            ]) { ContextSkippedFilePaths = ["src/Huge.cs"] };

        await PostAsync(result, factory);

        var summary = Assert.Single(messages, message => message.StartsWith(SummaryPrefix, StringComparison.Ordinal));
        Assert.StartsWith(SummaryPrefix + "\n\n" + result.Summary, summary);
        Assert.Contains("- **Warning** · `src/A.cs:7`: Guard the request.", summary);
        Assert.Contains("- **Info** · PR-level: Check the shared setting.", summary);
        Assert.Contains("| 2 | Medium: 1, Unassessed: 1 | High: 1, Unassessed: 1 |", summary);
        Assert.Contains("Skipped — exceeds model context window", summary);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, summary);
        var inline = Assert.Single(messages, message => message.StartsWith("![Severity: Warning]", StringComparison.Ordinal));
        Assert.Contains("<tr><th>Assessment</th><th>Level</th><th>Reason</th></tr>", inline);
        Assert.Contains("<tr><td>Confidence</td><td>High</td><td>The failing branch is visible.</td></tr>", inline);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_LongInlineDescriptionsDoNotTruncateSummaryTotalsOrCoverage()
    {
        var messages = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            var outbound = AdoCommentPoster.TruncateIfNeeded(message, TestPostedCommentComposer.Default);
            messages.Add(outbound);
            return Task.FromResult(CreatedThread(messages.Count, outbound));
        };
        var comments = Enumerable.Range(1, 8)
            .Select(index => new ReviewComment(
                $"src/File{index}.cs", index, CommentSeverity.Warning,
                $"Finding {index}: " + new string('x', 4000)))
            .ToArray();
        var result = new ReviewResult("Preserve the complete review conclusion.", comments)
        {
            BudgetSoftCapped = true,
            BudgetSoftCapThresholdUsd = 0.75m,
            BudgetSoftCapSpentUsd = 0.80m,
            BudgetSoftCapSkippedFilePaths = ["src/BudgetSkipped.cs"],
            ContextDegradedFilePaths = ["src/DiffOnly.cs"],
            ContextSkippedFilePaths = ["src/Huge.cs"],
        };

        await PostAsync(result, factory);

        var summary = Assert.Single(messages, message => message.StartsWith(SummaryPrefix, StringComparison.Ordinal));
        Assert.StartsWith(SummaryPrefix + "\n\n" + result.Summary, summary);
        Assert.Contains("| 8 | Unassessed: 8 | Unassessed: 8 |", summary);
        Assert.Contains("Budget soft cap reached", summary);
        Assert.Contains("src/BudgetSkipped.cs", summary);
        Assert.Contains("Reviewed diff-only", summary);
        Assert.Contains("src/DiffOnly.cs", summary);
        Assert.Contains("Skipped — exceeds model context window", summary);
        Assert.Contains("src/Huge.cs", summary);
        Assert.DoesNotContain("Review comment truncated", summary);
        Assert.True(summary.Length < AdoCommentPoster.MaxCommentLength);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, summary);
        foreach (var finding in comments)
        {
            Assert.Contains(messages, message => message.Contains("\n\n" + finding.Message, StringComparison.Ordinal));
        }
    }

    // ProPR writes the tenant's maximum review duration and the files the review did not reach into the Azure
    // DevOps summary, as it does for the plain-markdown providers.
    [Fact]
    public async Task PostResolvedThreadsAsync_StatesTheTimeLimitAndTheFilesNotReviewedInTheSummary()
    {
        var messages = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            messages.Add(message);
            return Task.FromResult(CreatedThread(messages.Count, message));
        };
        var result = new ReviewResult("The review covered the service layer.", [])
        {
            TimeLimitReached = true,
            TimeLimitSeconds = 900,
            TimeLimitSkippedFilePaths = ["src/NotReached.cs"],
        };

        await PostAsync(result, factory);

        var summary = Assert.Single(messages, message => message.StartsWith(SummaryPrefix, StringComparison.Ordinal));
        Assert.Contains("Time limit reached", summary);
        Assert.Contains("tenant's time limit of 15 minutes", summary);
        Assert.Contains("1 file was not reviewed", summary);
        Assert.Contains("src/NotReached.cs", summary);
    }

    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public async Task PostResolvedThreadsAsync_UsesBadgesInlineAndFixFirstInTheSummary(FindingBadgeStyle style)
    {
        var messages = new List<string>();
        var contexts = new List<CommentThreadContext?>();
        AdoCommentPoster.AdoThreadFactory factory = (message, context, _, _) =>
        {
            messages.Add(message);
            contexts.Add(context);
            return Task.FromResult(CreatedThread(messages.Count, message));
        };
        var finding = new ReviewComment("src/A.cs", 7, CommentSeverity.Error, "The request loses tenant data.")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Critical, FindingLikelihood.Likely, "Tenant rows are deleted.", "Every request uses this query.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, "The complete query is visible."),
            FindingClassification = new(
                [FindingTypeTaxonomy.LogicError], [], null, null, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
        };
        var result = new ReviewResult("Preserve this conclusion.", [finding]);

        var diagnostics = await PostAsync(result, factory, badgeStyle: style, changeTrackingIds: new Dictionary<string, int> { ["/src/A.cs"] = 1 });

        var inline = Assert.Single(messages, message => !message.StartsWith(SummaryPrefix, StringComparison.Ordinal));
        Assert.StartsWith(style == FindingBadgeStyle.Plain ? "`Severity: Error`" : "![Severity: Error]", inline);
        var header = inline.Split("\n\n", StringSplitOptions.None)[0];
        var positions = new[] { "Severity: Error", "Risk: Critical", "Impact: Critical", "Likelihood: Likely", "Confidence: High", "Type: Logic error" }
            .Select(label => header.IndexOf(label, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.DoesNotContain("Highest assessed priority", inline);
        Assert.DoesNotContain("Fix first", inline);
        Assert.Contains("<tr><td>Risk</td><td>Critical</td>", inline);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, inline);
        var summary = Assert.Single(messages, message => message.StartsWith(SummaryPrefix, StringComparison.Ordinal));
        Assert.StartsWith(SummaryPrefix + "\n\n**Fix first · Risk: Critical** · `src/A.cs:7`: " + finding.Message + "\n\n" + result.Summary, summary);
        Assert.Contains("- **Error** · `src/A.cs:7`: " + finding.Message, summary);
        Assert.Contains("| 1 | Critical: 1 | High: 1 |", summary);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, summary);
        Assert.Equal("/src/A.cs", contexts[1]!.FilePath);
        Assert.Equal(7, contexts[1]!.RightFileStart.Line);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal("The request loses tenant data.", finding.Message);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task PostResolvedThreadsAsync_ExistingAssessedFindingIsSuppressedAcrossLineEndings(string newline)
    {
        var finding = new ReviewComment("src/A.cs", 7, CommentSeverity.Error, "Guard the shared service before use.")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible,
                "The request fails.", "The optional service can be absent.", "review_model", FindingRiskAssessment.CurrentRubricVersion,
                FindingConfidence.High, "The dereference precedes validation."),
        };
        var body = AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default);
        var existing = new List<PrCommentThread>(BotSummaryThread())
        {
            new("73", "/src/A.cs", 7, [new PrThreadComment("Meister Bot", body.Replace("\n", newline, StringComparison.Ordinal), PosterBotId)]),
        };
        var messages = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            messages.Add(message);
            return Task.FromResult(CreatedThread(messages.Count, message));
        };

        var diagnostics = await PostAsync(new ReviewResult("Review.", [finding]), factory, existing);

        Assert.Empty(messages);
        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(1, diagnostics.SuppressedCount);
    }

    [Fact]
    public void BuildSummaryText_QuotedShellVariables_RemainReadable()
    {
        var result = new ReviewResult(
            "Run dotnet \"$ProCursorDll\" --output \"$ApiDll\" to validate the fix.",
            []);

        var summary = AdoCommentPoster.BuildSummaryText(result, TestPostedCommentComposer.Default);

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

        var summary = AdoCommentPoster.BuildSummaryText(result, TestPostedCommentComposer.Default);

        Assert.Contains("Reviewed diff-only", summary);
        Assert.Contains("src/BigService.cs", summary);
        Assert.Contains("Skipped — exceeds model context window", summary);
        Assert.Contains("src/Generated/Huge.g.cs", summary);
    }

    [Fact]
    public void BuildSummaryText_OmitsContextBudgetSectionsWhenNoneApply()
    {
        var result = new ReviewResult("Overall summary.", []);

        var summary = AdoCommentPoster.BuildSummaryText(result, TestPostedCommentComposer.Default);

        Assert.DoesNotContain("Reviewed diff-only", summary);
        Assert.DoesNotContain("Skipped — exceeds model context window", summary);
    }

    [Theory]
    [InlineData(CommentSeverity.Error, "Error")]
    [InlineData(CommentSeverity.Warning, "Warning")]
    public void FormatInlineCommentBody_UnsafeMarkup_IsNeutralizedWithoutEncodingQuotes(
        CommentSeverity severity,
        string expectedPrefix)
    {
        var comment = new ReviewComment(
            "/src/Foo.cs",
            7,
            severity,
            "Use \"$ApiDll\" after removing <script>alert('xss')</script>.");

        var body = AdoCommentPoster.FormatInlineCommentBody(comment, TestPostedCommentComposer.Default);

        Assert.StartsWith($"![Severity: {expectedPrefix}]", body, StringComparison.Ordinal);
        Assert.Contains("\"$ApiDll\"", body);
        Assert.DoesNotContain("&quot;", body);
        Assert.Equal(-1, body.IndexOf("<script>", StringComparison.Ordinal));
        Assert.Contains("<\u200Bscript>", body);
    }

    [Fact]
    public void BuildSummaryText_KeepsTheSummaryPrefixFirstAndTheMarkerLast()
    {
        var result = new ReviewResult("Overall summary.", []);

        var summary = AdoCommentPoster.BuildSummaryText(result, TestPostedCommentComposer.Default);

        Assert.StartsWith(HarvestedThreadEligibility.SummaryPrefix, summary, StringComparison.Ordinal);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSummaryText_PutsTheMarkerAfterTheContextBudgetSections()
    {
        var result = new ReviewResult("Overall summary.", [])
        {
            ContextSkippedFilePaths = ["src/Generated/Huge.g.cs"],
        };

        var summary = AdoCommentPoster.BuildSummaryText(result, TestPostedCommentComposer.Default);

        Assert.EndsWith(TestPostedCommentComposer.Default.Text, summary, StringComparison.Ordinal);
        Assert.True(
            summary.IndexOf("src/Generated/Huge.g.cs", StringComparison.Ordinal)
            < summary.IndexOf(TestPostedCommentComposer.Default.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSummaryText_WithConfiguredWording_UsesThatWording()
    {
        var composer = TestPostedCommentComposer.With("Erzeugt von ProPR.");
        var result = new ReviewResult("Overall summary.", []);

        var summary = AdoCommentPoster.BuildSummaryText(result, composer);

        Assert.EndsWith(composer.Text, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatInlineCommentBody_KeepsTheClassificationFirstAndTheMarkerLast()
    {
        var comment = new ReviewComment("/src/Foo.cs", 7, CommentSeverity.Error, "Add a null check.");

        var body = AdoCommentPoster.FormatInlineCommentBody(comment, TestPostedCommentComposer.Default);

        Assert.StartsWith("![Severity: Error]", body, StringComparison.Ordinal);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, body, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatInlineCommentBody_AssessedFindingPutsSafeEvidenceInsideClosedDetails()
    {
        var comment = new ReviewComment("/src/Foo.cs", 7, CommentSeverity.Warning, "Guard the service. </details><script>bad</script>")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Moderate, FindingLikelihood.Possible, "Fails. </details>", "On retry.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Medium, "The caller is visible."),
        };

        var body = AdoCommentPoster.FormatInlineCommentBody(comment, TestPostedCommentComposer.Distinctive);

        Assert.StartsWith("![Severity: Warning]", body);
        Assert.Contains("![Risk: Medium]", body);
        Assert.Contains("\n\nGuard the service.", body);
        Assert.Contains(
            "<!-- propr-risk-assessment:start -->\n\n<details><summary>Assessment details</summary><table><tr><th>Assessment</th><th>Level</th><th>Reason</th></tr>",
            body);
        Assert.Contains("<tr><td>Impact</td><td>Moderate</td><td>Fails. &lt;/details&gt;</td></tr>", body, StringComparison.Ordinal);
        var section = body[body.IndexOf("<details>", StringComparison.Ordinal)..body.IndexOf("</details>", StringComparison.Ordinal)];
        Assert.DoesNotContain('\n', section);
        Assert.DoesNotContain('\r', section);
        Assert.Contains("<\u200B/details>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
        Assert.Equal(1, body.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, body.Split("</details>", StringSplitOptions.None).Length - 1);
        Assert.True(body.IndexOf("Guard the service.", StringComparison.Ordinal) < body.IndexOf("<details>", StringComparison.Ordinal));
        Assert.True(body.IndexOf("</details>", StringComparison.Ordinal) < body.IndexOf(TestPostedCommentComposer.DistinctiveMarker, StringComparison.Ordinal));
        TestPostedCommentComposer.AssertMarkedOnce(body, TestPostedCommentComposer.DistinctiveMarker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncateIfNeeded_LargeAssessedFindingKeepsGeneratedWrappersAndSignatureBalanced(bool longMessage)
    {
        var largeText = string.Concat(Enumerable.Repeat("recorded evidence ", 12000));
        var message = longMessage ? largeText : new string('x', AdoCommentPoster.MaxCommentLength - 600);
        var comment = new ReviewComment("/src/Foo.cs", 7, CommentSeverity.Warning, message)
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, new string('e', FindingRiskAssessment.MaxReasonLength), "On retry.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion),
        };
        var marked = AdoCommentPoster.FormatInlineCommentBody(comment, TestPostedCommentComposer.Distinctive);
        Assert.True(marked.Length > AdoCommentPoster.MaxCommentLength);

        var body = AdoCommentPoster.TruncateIfNeeded(marked, TestPostedCommentComposer.Distinctive);

        Assert.True(body.Length <= AdoCommentPoster.MaxCommentLength);
        Assert.Contains("truncated", body);
        Assert.Equal(body.Split("<details>", StringSplitOptions.None).Length, body.Split("</details>", StringSplitOptions.None).Length);
        Assert.Equal(
            body.Split("<!-- propr-risk-assessment:start -->", StringSplitOptions.None).Length,
            body.Split("<!-- propr-risk-assessment:end -->", StringSplitOptions.None).Length);
        var originalAssessment =
            marked[
                marked.IndexOf("<!-- propr-risk-assessment:start -->", StringComparison.Ordinal)..marked.IndexOf(
                    "<!-- propr-risk-assessment:end -->", StringComparison.Ordinal)]
            + "<!-- propr-risk-assessment:end -->";
        Assert.Contains(originalAssessment, body, StringComparison.Ordinal);

        TestPostedCommentComposer.AssertMarkedOnce(body, TestPostedCommentComposer.DistinctiveMarker);
    }

    [Fact]
    public void FormatInlineCommentBody_HtmlCellsPreserveInlineCodeAndBlockUnsafeMarkupAndUrls()
    {
        const string reason =
            "Use `Task<T>` and `a\\|b`.\n</td></tr></table></details><script>bad</script> [bad](javascript:alert) ![bad](data:image/svg+xml,bad)";
        var finding = new ReviewComment(null, null, CommentSeverity.Warning, "Preserve the complete PR-level finding.")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Moderate, FindingLikelihood.Possible, reason, "# Heading\n> Quote", "review_model", FindingRiskAssessment.CurrentRubricVersion,
                FindingConfidence.Medium, "[reference](https://example.com/x)"),
        };

        var body = AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default);

        Assert.Contains("<code>Task&lt;T&gt;</code>", body, StringComparison.Ordinal);
        Assert.Contains("<code>a\\|b</code>", body, StringComparison.Ordinal);
        Assert.Contains("&lt;/td&gt;&lt;/tr&gt;&lt;/table&gt;&lt;/details&gt;&lt;script&gt;bad&lt;/script&gt;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"javascript:", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"data:", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<blockquote>", body, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/x\"", body, StringComparison.Ordinal);
        Assert.Equal(reason, finding.RiskAssessment.ImpactReason);
        TestPostedCommentComposer.AssertMarkedOnce(body, TestPostedCommentComposer.Default.Text);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_FilelessAssessedFindingUsesCompleteCompactHtml()
    {
        var messages = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            messages.Add(message);
            return Task.FromResult(CreatedThread(messages.Count, message));
        };
        var finding = new ReviewComment(null, null, CommentSeverity.Warning, "Retain the PR-level finding.")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, "Service fails.", "On retry.", "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };

        await PostAsync(new ReviewResult("Conclusion.", [finding]) { ContextSkippedFilePaths = ["src/Huge.cs"] }, factory);

        var findingBody = Assert.Single(messages, message => message.StartsWith("![Severity: Warning]", StringComparison.Ordinal));
        Assert.Contains("Retain the PR-level finding.", findingBody);
        Assert.Contains("<details><summary>Assessment details</summary><table>", findingBody, StringComparison.Ordinal);
        Assert.Contains("<tr><td>Impact</td><td>High</td><td>Service fails.</td></tr>", findingBody, StringComparison.Ordinal);
        Assert.Contains(messages, message => message.Contains("src/Huge.cs", StringComparison.Ordinal));
        TestPostedCommentComposer.AssertMarkedOnce(findingBody, TestPostedCommentComposer.Default.Text);
    }

    [Fact]
    public void FormatterTruncate_WholeHtmlAssessmentExceedingTheAvailableCapIsDropped()
    {
        var finding = new ReviewComment(null, null, CommentSeverity.Warning, "Keep the description.")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, "Evidence.", "On retry.", "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };
        var rendered = TestPostedCommentComposer.Default.Strip(AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default));

        var body = MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services.ReviewFindingTextFormatter.Truncate(rendered, 180);

        Assert.True(body.Length <= 180);
        Assert.Contains("Keep the description.", body);
        Assert.DoesNotContain("<details>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("propr-risk-assessment", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncateIfNeeded_RechecksCodeSpansBeforeAppendingTheWholeHtmlAssessment(bool assessed)
    {
        var description = "Use `<details>Hidden</details> " + string.Concat(Enumerable.Repeat("recorded evidence ", 12000)) + "` after checking.";
        var finding = new ReviewComment(null, null, CommentSeverity.Warning, description)
        {
            RiskAssessment = assessed
                ? new FindingRiskAssessment(
                    FindingImpact.High, FindingLikelihood.Possible, "Evidence.", "On retry.", "review_model", FindingRiskAssessment.CurrentRubricVersion)
                : null,
        };

        var body = AdoCommentPoster.TruncateIfNeeded(
            AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default), TestPostedCommentComposer.Default);
        var html = Markdig.Markdown.ToHtml(body);

        Assert.True(body.Length <= AdoCommentPoster.MaxCommentLength);
        Assert.DoesNotContain("<details>Hidden</details>", html, StringComparison.Ordinal);
        if (assessed)
        {
            Assert.Contains("<summary>Assessment details</summary><table>", html, StringComparison.Ordinal);
            Assert.Contains("<tr><td>Confidence</td>", html, StringComparison.Ordinal);
        }

        TestPostedCommentComposer.AssertMarkedOnce(body, TestPostedCommentComposer.Default.Text);
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_KeepsTheNoticeAndTheMarkerInsideTheCap()
    {
        var marked = AdoCommentPoster.FormatInlineCommentBody(
            new ReviewComment("/src/Foo.cs", 7, CommentSeverity.Error, new string('a', AdoCommentPoster.MaxCommentLength)),
            TestPostedCommentComposer.Default);

        var result = AdoCommentPoster.TruncateIfNeeded(marked, TestPostedCommentComposer.Default);

        Assert.True(result.Length <= AdoCommentPoster.MaxCommentLength);
        Assert.Contains("truncated", result, StringComparison.Ordinal);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, result, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_CarriesTheMarkerOnlyOnce()
    {
        // The body goes in already marked, so a truncation that appends without taking the marker off first
        // leaves two of them. Comparing the first and last occurrence alone would also hold for a body that
        // lost the marker entirely, which is why the marker has to be found on the last line as well.
        var marked = AdoCommentPoster.FormatInlineCommentBody(
            new ReviewComment("/src/Foo.cs", 7, CommentSeverity.Error, new string('a', AdoCommentPoster.MaxCommentLength)),
            TestPostedCommentComposer.Distinctive);

        var result = AdoCommentPoster.TruncateIfNeeded(marked, TestPostedCommentComposer.Distinctive);

        TestPostedCommentComposer.AssertMarkedOnce(result, TestPostedCommentComposer.DistinctiveMarker);
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
        AdoCommentPoster.AdoThreadFactory factory = (message, threadContext, _, _) =>
        {
            invocations.Add(message);
            return threadContext?.FilePath == "/src/B.cs"
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
        Assert.Equal(1, failure.FindingOrdinal);
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

    // An admission refusal carries its whole message in the summary. Where a bot summary thread already sits on
    // the pull request, skipping the summary would leave the author with nothing saying why nothing ran, so the
    // refusal joins that thread as a reply.
    [Fact]
    public async Task PostResolvedThreadsAsync_WithAReplyFactoryAndABotSummary_RepliesInThatThread()
    {
        var newThreads = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            newThreads.Add(message);
            return Task.FromResult(CreatedThread(newThreads.Count, message));
        };

        var replies = new List<(int ThreadId, string Message)>();
        AdoCommentPoster.AdoReplyFactory replyFactory = (threadId, message, _) =>
        {
            replies.Add((threadId, message));
            return Task.FromResult(new Comment { Id = 77, Content = message });
        };

        var diagnostics = await PostAsync(new ReviewResult(Refusal, []), factory, BotSummaryThread(), replyFactory);

        Assert.Empty(newThreads);
        var reply = Assert.Single(replies);
        Assert.Equal(4242, reply.ThreadId);
        Assert.Equal(
            TestPostedCommentComposer.Default.Append(
                SummaryPrefix + "\n\n" + Refusal + "\n\n| Findings to report | Risk levels | Confidence levels |\n| --- | --- | --- |\n| 0 | None | None |"),
            reply.Message);
        // The reply is recorded against the thread it joined, so the provenance row points at that thread.
        var posted = Assert.Single(diagnostics.PostedComments);
        Assert.Equal("4242", posted.ProviderThreadId);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_ReplyRejected_RecordsTheFailureAndStillPostsTheInlineThreads()
    {
        // A rejected reply is caught where the summary is posted, separately from a rejected thread creation,
        // so the rest of the pass has to survive it the same way.
        var newThreads = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            newThreads.Add(message);
            return Task.FromResult(CreatedThread(newThreads.Count, message));
        };

        AdoCommentPoster.AdoReplyFactory replyFactory = (_, _, _) =>
            throw new InvalidOperationException("TF401028: the reply was rejected by the provider.");

        var result = new ReviewResult(
            Refusal,
            new List<ReviewComment>
            {
                new("/src/A.cs", 1, CommentSeverity.Error, "inline still posts"),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, factory, BotSummaryThread(), replyFactory);

        var posted = Assert.Single(newThreads);
        Assert.Contains("inline still posts", posted, StringComparison.Ordinal);
        Assert.Equal(1, diagnostics.PostedCount);
        var failure = Assert.Single(diagnostics.PostingFailures);
        Assert.Equal("summary", failure.ThreadKind);
        Assert.Null(failure.FilePath);
        Assert.Contains("TF401028", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_ReplyRejectedAndNothingElseToPost_ThrowsPublicationFailure()
    {
        // An admission refusal carries no inline comments, so a rejected reply leaves the pass with nothing
        // posted and it must not be reported as a success.
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) => Task.FromResult(CreatedThread(1, message));
        AdoCommentPoster.AdoReplyFactory replyFactory = (_, _, _) =>
            throw new InvalidOperationException("TF401028: the reply was rejected by the provider.");

        var exception = await Assert.ThrowsAsync<ReviewCommentPublicationFailedException>(() => PostAsync(
            new ReviewResult(Refusal, []), factory, BotSummaryThread(), replyFactory));

        Assert.Equal(0, exception.Diagnostics.PostedCount);
        Assert.Equal(1, exception.Diagnostics.FailedCount);
        Assert.Equal("summary", Assert.Single(exception.Diagnostics.PostingFailures).ThreadKind);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_WithAReplyFactoryAndNoBotSummary_PostsTheSummaryThread()
    {
        var newThreads = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            newThreads.Add(message);
            return Task.FromResult(CreatedThread(newThreads.Count, message));
        };

        var replied = false;
        AdoCommentPoster.AdoReplyFactory replyFactory = (_, _, _) =>
        {
            replied = true;
            return Task.FromResult(new Comment { Id = 1, Content = string.Empty });
        };

        await PostAsync(new ReviewResult(OversizedRepository, []), factory, existingThreads: [], replyFactory);

        Assert.False(replied);
        Assert.Equal(
            TestPostedCommentComposer.Default.Append(
                SummaryPrefix + "\n\n" + OversizedRepository +
                "\n\n| Findings to report | Risk levels | Confidence levels |\n| --- | --- | --- |\n| 0 | None | None |"),
            Assert.Single(newThreads));
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_WithoutAReplyFactory_LeavesAnExistingBotSummaryAlone()
    {
        var newThreads = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            newThreads.Add(message);
            return Task.FromResult(CreatedThread(newThreads.Count, message));
        };

        await PostAsync(new ReviewResult("Summary body.", []), factory, BotSummaryThread());

        Assert.Empty(newThreads);
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostAsync(result, factory, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_InlineTimeout_IsIsolatedNotPropagated()
    {
        // A provider request timeout surfaces as TaskCanceledException (an OperationCanceledException) even
        // though the caller never cancelled — it must be isolated like any other rejection, not abort the pass.
        var invocations = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, threadContext, _, _) =>
        {
            invocations.Add(message);
            return threadContext?.FilePath == "/src/B.cs"
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

    [Fact]
    public async Task PostResolvedThreadsAsync_FilelessFindingWithExistingBotSummary_PostsInsteadOfSuppressing()
    {
        // A later review increment already carries the bot's pull-request-level summary thread. A finding
        // without a file anchor must still reach the provider instead of being counted as a duplicate.
        var invocations = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            invocations.Add(message);
            return Task.FromResult(CreatedThread(invocations.Count, message));
        };

        var existingThreads = new List<PrCommentThread>
        {
            new(
                "101",
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "**AI Review Summary**\n\nEarlier increment.", PosterBotId),
                }.AsReadOnly()),
        };

        var result = new ReviewResult(
            "Second increment.",
            new List<ReviewComment>
            {
                new(null, null, CommentSeverity.Warning, "Two callers duplicate the retry policy."),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, factory, existingThreads: existingThreads);

        // The summary is skipped because one already exists, so the only creation is the fileless finding.
        var posted = Assert.Single(invocations);
        Assert.Contains("Two callers duplicate the retry policy.", posted, StringComparison.Ordinal);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
        Assert.Empty(diagnostics.SuppressionReasons);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_FirstIncrementFilelessFinding_PostsSummaryAndFinding()
    {
        // On the first increment the summary thread is created during this same pass, so it is absent from
        // the fetched thread list and cannot suppress anything.
        var invocations = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            invocations.Add(message);
            return Task.FromResult(CreatedThread(invocations.Count, message));
        };

        var result = new ReviewResult(
            "First increment.",
            new List<ReviewComment>
            {
                new(null, null, CommentSeverity.Warning, "Two callers duplicate the retry policy."),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, factory, existingThreads: []);

        Assert.Equal(2, invocations.Count);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_FilelessFindingWithHumanRepliedSummary_StillPosts()
    {
        // A human reply on the summary thread does not make it a match for an unrelated fileless finding.
        var invocations = new List<string>();
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            invocations.Add(message);
            return Task.FromResult(CreatedThread(invocations.Count, message));
        };

        var existingThreads = new List<PrCommentThread>
        {
            new(
                "102",
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "**AI Review Summary**\n\nEarlier increment.", PosterBotId),
                    new("Alice", "Thanks, looking at this now.", Guid.NewGuid()),
                }.AsReadOnly(),
                "Closed"),
        };

        var result = new ReviewResult(
            "Second increment.",
            new List<ReviewComment>
            {
                new(null, null, CommentSeverity.Warning, "Two callers duplicate the retry policy."),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, factory, existingThreads: existingThreads);

        var posted = Assert.Single(invocations);
        Assert.Contains("Two callers duplicate the retry policy.", posted, StringComparison.Ordinal);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    private static Task<ReviewCommentPostingDiagnosticsDto> PostAsync(
        ReviewResult result,
        AdoCommentPoster.AdoThreadFactory factory,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        AdoCommentPoster.AdoReplyFactory? replyFactory = null,
        CancellationToken cancellationToken = default,
        FindingBadgeStyle badgeStyle = FindingBadgeStyle.ShieldsIo,
        IReadOnlyDictionary<string, int>? changeTrackingIds = null)
    {
        var poster = new AdoCommentPoster(null!, null!, TestPostedCommentComposer.Default);
        return poster.PostResolvedThreadsAsync(
            result,
            factory,
            PosterBotId,
            clientId: null,
            organizationUrl: Host,
            projectId: Project,
            repositoryId: "repo",
            pullRequestId: 1,
            iterationId: 1,
            compareToIterationId: null,
            changeTrackingIds: changeTrackingIds ?? new Dictionary<string, int>(),
            existingThreads: existingThreads,
            publicationIdentity: null,
            replyFactory: replyFactory,
            cancellationToken,
            badgeStyle: badgeStyle);
    }

    private static IReadOnlyList<PrCommentThread> BotSummaryThread(string threadId = "4242")
    {
        return
        [
            new PrCommentThread(
                threadId,
                null,
                null,
                [new PrThreadComment("Meister Bot", "**AI Review Summary**\n\nAll clear.", PosterBotId)]),
        ];
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
