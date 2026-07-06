// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for the deterministic per-file post-processing steps that remain after language-robust
///     screening replaced the English phrase filters: <c>StripInfoComments</c> and <c>ApplyConfidenceFloor</c>.
///     Both are exercised via the internal static methods exposed through <c>InternalsVisibleTo</c> on the
///     Infrastructure assembly.
/// </summary>
public class FileByFileReviewOrchestratorPostProcessingTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ReviewResult MakeResult(params ReviewComment[] comments)
    {
        return new ReviewResult("summary text", comments.ToList().AsReadOnly());
    }

    private static ReviewComment MakeComment(CommentSeverity severity, string message, string? filePath = "src/Foo.cs")
    {
        return new ReviewComment(filePath, 1, severity, message);
    }

    // ── INFO strip ───────────────────────────────────────────────────────────────

    [Fact]
    public void StripInfoComments_SingleInfoComment_ReturnsEmptyList()
    {
        var result = MakeResult(MakeComment(CommentSeverity.Info, "Good use of dependency injection."));

        var stripped = FileByFileReviewOrchestrator.StripInfoComments(result);

        Assert.Empty(stripped.Comments);
    }

    [Fact]
    public void StripInfoComments_WarningComment_IsRetained()
    {
        var comment = MakeComment(CommentSeverity.Warning, "Potential null dereference.");
        var result = MakeResult(comment);

        var stripped = FileByFileReviewOrchestrator.StripInfoComments(result);

        Assert.Single(stripped.Comments);
        Assert.Same(comment, stripped.Comments[0]);
    }

    [Fact]
    public void StripInfoComments_MixedList_DropsOnlyInfo()
    {
        var errorComment = MakeComment(CommentSeverity.Error, "SQL injection on line 5.");
        var infoComment = MakeComment(CommentSeverity.Info, "Nice abstraction.");
        var result = MakeResult(errorComment, infoComment);

        var stripped = FileByFileReviewOrchestrator.StripInfoComments(result);

        Assert.Single(stripped.Comments);
        Assert.Same(errorComment, stripped.Comments[0]);
    }

    [Fact]
    public void StripInfoComments_WithNoInfoComments_ReturnsSameInstance()
    {
        var comment = MakeComment(CommentSeverity.Warning, "Missing null check.");
        var result = MakeResult(comment);

        var stripped = FileByFileReviewOrchestrator.StripInfoComments(result);

        Assert.Same(result, stripped);
    }

    // ── ApplyConfidenceFloor ──────────────────────────────────────────────────────

    private static AiReviewOptions DefaultOpts()
    {
        return new AiReviewOptions { ConfidenceFloorError = 80, ConfidenceFloorWarning = 60 };
    }

    [Fact]
    public void ApplyConfidenceFloor_ErrorBelowFloor_DowngradesToWarning()
    {
        var result = MakeResult(MakeComment(CommentSeverity.Error, "Confirmed null ref."));

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, 79, DefaultOpts());

        Assert.Equal(CommentSeverity.Warning, adjusted.Comments[0].Severity);
    }

    [Fact]
    public void ApplyConfidenceFloor_WarningBelowFloor_DowngradesToSuggestion()
    {
        var result = MakeResult(MakeComment(CommentSeverity.Warning, "Missing transaction boundary."));

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, 59, DefaultOpts());

        Assert.Equal(CommentSeverity.Suggestion, adjusted.Comments[0].Severity);
    }

    [Fact]
    public void ApplyConfidenceFloor_ErrorAtFloor_IsUnchanged()
    {
        var comment = MakeComment(CommentSeverity.Error, "SQL injection at line 5.");
        var result = MakeResult(comment);

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, 80, DefaultOpts());

        Assert.Same(result, adjusted); // no change = same instance
    }

    [Fact]
    public void ApplyConfidenceFloor_NullConfidence_DoesNotDowngrade()
    {
        var comment = MakeComment(CommentSeverity.Error, "Confirmed defect.");
        var result = MakeResult(comment);

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, null, DefaultOpts());

        Assert.Same(result, adjusted);
        Assert.Equal(CommentSeverity.Error, adjusted.Comments[0].Severity);
    }

    [Fact]
    public void ApplyConfidenceFloor_SuggestionComment_NeverDowngraded()
    {
        var comment = MakeComment(CommentSeverity.Suggestion, "Replace X with Y.");
        var result = MakeResult(comment);

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, 0, DefaultOpts());

        // SUGGESTION is the lowest severity — no further downgrade exists
        Assert.Equal(CommentSeverity.Suggestion, adjusted.Comments[0].Severity);
    }

    [Fact]
    public void ApplyConfidenceFloor_MixedComments_EachEvaluatedIndependently()
    {
        // confidence=79 → ERRORs downgrade to WARNING; WARNINGs stay (79 >= 60)
        var result = MakeResult(
            MakeComment(CommentSeverity.Error, "Error comment."),
            MakeComment(CommentSeverity.Warning, "Warning comment."),
            MakeComment(CommentSeverity.Suggestion, "Suggestion comment."));

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, 79, DefaultOpts());

        Assert.Equal(CommentSeverity.Warning, adjusted.Comments[0].Severity); // ERROR → WARNING
        Assert.Equal(CommentSeverity.Warning, adjusted.Comments[1].Severity); // WARNING unchanged (79 >= 60)
        Assert.Equal(CommentSeverity.Suggestion, adjusted.Comments[2].Severity); // SUGGESTION unchanged
    }
}
