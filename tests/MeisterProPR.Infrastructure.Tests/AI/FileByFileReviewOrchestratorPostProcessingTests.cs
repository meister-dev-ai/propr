// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for the per-file post-processing filters added in feature 023:
///     <list type="bullet">
///       <item>T006/T007/T008 — <c>FilterSpeculativeComments</c> (US1)</item>
///       <item>T009/T010       — INFO strip (US2)</item>
///       <item>T011/T012       — <c>FilterVagueSuggestions</c> (US3)</item>
///       <item>T015/T016/T017  — <c>ApplyConfidenceFloor</c> (US5)</item>
///     </list>
///     All methods are tested via the internal static methods exposed through
///     <c>InternalsVisibleTo</c> on the Infrastructure assembly.
/// </summary>
public class FileByFileReviewOrchestratorPostProcessingTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ReviewResult MakeResult(params ReviewComment[] comments) =>
        new("summary text", comments.ToList().AsReadOnly());

    private static ReviewComment MakeComment(CommentSeverity severity, string message, string? filePath = "src/Foo.cs") =>
        new(filePath, 1, severity, message);

    // ── T006: FilterSpeculativeComments — hedge phrase detection ─────────────────

    [Theory]
    [InlineData("please verify that this is correct")]
    [InlineData("worth checking whether X is set")]
    [InlineData("this may be a bug in the logic")]
    [InlineData("If Your file contains the secret")]        // case-insensitive
    [InlineData("validate that the token is valid")]
    [InlineData("it appears the config is missing")]
    [InlineData("it seems like the null check was omitted")]
    [InlineData("unclear whether the lock is acquired")]
    [InlineData("i cannot confirm whether this is safe")]
    [InlineData("consider whether a transaction is needed")]
    [InlineData("this could be improved with caching")]
    [InlineData("you may want to add logging here")]
    [InlineData("worth verifying the schema exists")]
    [InlineData("if applicable, add a retry policy")]
    [InlineData("if the file contains credentials")]
    [InlineData("if [FileName] contains the key")]
    public void FilterSpeculativeComments_DropsCommentWithHedgePhrase(string message)
    {
        var result = MakeResult(MakeComment(CommentSeverity.Warning, message));

        var filtered = FileByFileReviewOrchestrator.FilterSpeculativeComments(result);

        Assert.Empty(filtered.Comments);
    }

    [Fact]
    public void FilterSpeculativeComments_RetainsCommentWithoutHedgePhrase()
    {
        var comment = MakeComment(CommentSeverity.Error, "The password is stored in plaintext on line 42.");
        var result = MakeResult(comment);

        var filtered = FileByFileReviewOrchestrator.FilterSpeculativeComments(result);

        Assert.Single(filtered.Comments);
        Assert.Same(comment, filtered.Comments[0]);
    }

    [Fact]
    public void FilterSpeculativeComments_WithOnlyCleanComments_ReturnsSameInstance()
    {
        var comment = MakeComment(CommentSeverity.Error, "Confirmed null dereference at line 10.");
        var result = MakeResult(comment);

        var filtered = FileByFileReviewOrchestrator.FilterSpeculativeComments(result);

        // When nothing was dropped, the same ReviewResult instance is returned.
        Assert.Same(result, filtered);
    }

    [Fact]
    public void FilterSpeculativeComments_WithEmptyComments_ReturnsEmptyResult()
    {
        var result = MakeResult();

        var filtered = FileByFileReviewOrchestrator.FilterSpeculativeComments(result);

        Assert.Empty(filtered.Comments);
    }

    [Fact]
    public void FilterSpeculativeComments_DropsHedgePhrasesAcrossAllSeverities()
    {
        var result = MakeResult(
            MakeComment(CommentSeverity.Error, "please verify this is safe"),
            MakeComment(CommentSeverity.Warning, "this may be a null ref"),
            MakeComment(CommentSeverity.Info, "it appears config is missing"),
            MakeComment(CommentSeverity.Suggestion, "worth checking if lock is held"));

        var filtered = FileByFileReviewOrchestrator.FilterSpeculativeComments(result);

        Assert.Empty(filtered.Comments);
    }

    // ── T009: INFO strip ─────────────────────────────────────────────────────────

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

    // ── T011: FilterVagueSuggestions ─────────────────────────────────────────────

    [Theory]
    [InlineData("consider refactoring this service into smaller methods")]
    [InlineData("consider adding a test for this edge case")]
    [InlineData("you could also validate the input here")]
    [InlineData("you might also add retry logic")]
    [InlineData("you might want to extract a helper method")]
    [InlineData("it would be worth adding observability")]
    [InlineData("it would be worth considering a cache")]
    [InlineData("would also be good to add documentation")]
    [InlineData("could be strengthened by adding error handling")]
    [InlineData("could be made more readable with constants")]
    [InlineData("could also verify the return value")]
    public void FilterVagueSuggestions_DropsSuggestionWithVaguePhrase(string message)
    {
        var result = MakeResult(MakeComment(CommentSeverity.Suggestion, message));

        var filtered = FileByFileReviewOrchestrator.FilterVagueSuggestions(result);

        Assert.Empty(filtered.Comments);
    }

    [Fact]
    public void FilterVagueSuggestions_DoesNotDropWarningWithVaguePhrase()
    {
        // "consider refactoring" should NOT filter a WARNING — only SUGGESTIONs are filtered
        var comment = MakeComment(CommentSeverity.Warning, "consider refactoring to avoid race condition");
        var result = MakeResult(comment);

        var filtered = FileByFileReviewOrchestrator.FilterVagueSuggestions(result);

        Assert.Single(filtered.Comments);
        Assert.Same(comment, filtered.Comments[0]);
    }

    [Fact]
    public void FilterVagueSuggestions_DoesNotDropErrorWithVaguePhrase()
    {
        var comment = MakeComment(CommentSeverity.Error, "you could also remove the deadlock here");
        var result = MakeResult(comment);

        var filtered = FileByFileReviewOrchestrator.FilterVagueSuggestions(result);

        Assert.Single(filtered.Comments);
    }

    [Fact]
    public void FilterVagueSuggestions_RetainsConcreteSuggestion()
    {
        var comment = MakeComment(CommentSeverity.Suggestion,
            "Replace Foo.GetById(id) with Foo.FindAsync(id) to avoid synchronous blocking on line 42.");
        var result = MakeResult(comment);

        var filtered = FileByFileReviewOrchestrator.FilterVagueSuggestions(result);

        Assert.Single(filtered.Comments);
        Assert.Same(comment, filtered.Comments[0]);
    }

    [Fact]
    public void FilterVagueSuggestions_WithNoVagueSuggestions_ReturnsSameInstance()
    {
        var comment = MakeComment(CommentSeverity.Suggestion,
            "Use IOptions<T> instead of direct environment variable access on line 15 to enable test overrides.");
        var result = MakeResult(comment);

        var filtered = FileByFileReviewOrchestrator.FilterVagueSuggestions(result);

        Assert.Same(result, filtered);
    }

    // ── T015: ApplyConfidenceFloor ────────────────────────────────────────────────

    private static AiReviewOptions DefaultOpts() => new() { ConfidenceFloorError = 80, ConfidenceFloorWarning = 60 };

    [Fact]
    public void ApplyConfidenceFloor_ErrorBelowFloor_DowngradesToWarning()
    {
        var result = MakeResult(MakeComment(CommentSeverity.Error, "Confirmed null ref."));

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, finalConfidence: 79, DefaultOpts());

        Assert.Equal(CommentSeverity.Warning, adjusted.Comments[0].Severity);
    }

    [Fact]
    public void ApplyConfidenceFloor_WarningBelowFloor_DowngradesToSuggestion()
    {
        var result = MakeResult(MakeComment(CommentSeverity.Warning, "Missing transaction boundary."));

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, finalConfidence: 59, DefaultOpts());

        Assert.Equal(CommentSeverity.Suggestion, adjusted.Comments[0].Severity);
    }

    [Fact]
    public void ApplyConfidenceFloor_ErrorAtFloor_IsUnchanged()
    {
        var comment = MakeComment(CommentSeverity.Error, "SQL injection at line 5.");
        var result = MakeResult(comment);

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, finalConfidence: 80, DefaultOpts());

        Assert.Same(result, adjusted); // no change = same instance
    }

    [Fact]
    public void ApplyConfidenceFloor_NullConfidence_DoesNotDowngrade()
    {
        var comment = MakeComment(CommentSeverity.Error, "Confirmed defect.");
        var result = MakeResult(comment);

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, finalConfidence: null, DefaultOpts());

        Assert.Same(result, adjusted);
        Assert.Equal(CommentSeverity.Error, adjusted.Comments[0].Severity);
    }

    [Fact]
    public void ApplyConfidenceFloor_SuggestionComment_NeverDowngraded()
    {
        var comment = MakeComment(CommentSeverity.Suggestion, "Replace X with Y.");
        var result = MakeResult(comment);

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, finalConfidence: 0, DefaultOpts());

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

        var adjusted = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, finalConfidence: 79, DefaultOpts());

        Assert.Equal(CommentSeverity.Warning, adjusted.Comments[0].Severity);    // ERROR → WARNING
        Assert.Equal(CommentSeverity.Warning, adjusted.Comments[1].Severity);    // WARNING unchanged (79 >= 60)
        Assert.Equal(CommentSeverity.Suggestion, adjusted.Comments[2].Severity); // SUGGESTION unchanged
    }
}
