// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Tests for <see cref="FindingDeduplicator" /> token-set Jaccard deduplication logic (US2).
/// </summary>
public class FindingDeduplicatorTests
{
    // T008 — Identical messages across different files → merged into one comment (FilePath=null)
    [Fact]
    public void Deduplicate_IdenticalMessagesAcrossFiles_MergesIntoSingle()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Use IDisposable pattern here"),
            new("src/B.cs", 2, CommentSeverity.Warning, "Use IDisposable pattern here"),
            new("src/C.cs", 3, CommentSeverity.Warning, "Use IDisposable pattern here"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Single(result);
        Assert.Null(result[0].FilePath);
        Assert.Contains("A.cs", result[0].Message);
        Assert.Contains("B.cs", result[0].Message);
        Assert.Contains("C.cs", result[0].Message);
    }

    // T008 — 80% similar messages across files → merged
    [Fact]
    public void Deduplicate_HighlySimilarMessagesAcrossFiles_Merges()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "This method should use async/await instead of blocking calls"),
            new("src/B.cs", 2, CommentSeverity.Warning, "This method should use async await instead of blocking calls"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Single(result);
        Assert.Null(result[0].FilePath);
    }

    // T008 — 50% similar messages → NOT merged (below threshold)
    [Fact]
    public void Deduplicate_LowSimilarityMessages_NotMerged()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Missing null check on input parameter foo"),
            new("src/B.cs", 2, CommentSeverity.Warning, "Consider extracting this into a separate helper class"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CollapseSameFileDuplicates_NearIdenticalSameFileFindings_CollapseToFirst()
    {
        // Two passes independently reporting the same issue on the same file (different anchor lines,
        // near-identical wording) collapse into a single comment, keeping the first occurrence.
        var comments = new List<ReviewComment>
        {
            new("src/A.vue", 86, CommentSeverity.Error, "renderMarkdown output is bound to v-html which is a stored XSS sink"),
            new("src/A.vue", 88, CommentSeverity.Error, "renderMarkdown output is bound to v-html which is a stored XSS sink here"),
        }.AsReadOnly();

        var result = FindingDeduplicator.CollapseSameFileDuplicates(comments);

        Assert.Single(result);
        Assert.Equal(86, result[0].LineNumber);
    }

    [Fact]
    public void CollapseSameFileDuplicates_DistinctSameFileFindings_BothSurvive()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 10, CommentSeverity.Warning, "Missing null check on the input parameter before dereference"),
            new("src/A.cs", 40, CommentSeverity.Warning, "Consider extracting this branch into a well named helper method"),
        }.AsReadOnly();

        var result = FindingDeduplicator.CollapseSameFileDuplicates(comments);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CollapseSameFileDuplicates_IdenticalMessagesOnDifferentFiles_BothSurvive()
    {
        // Cross-file collapse is the job of Deduplicate; same-file collapse leaves different files alone.
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Use IDisposable pattern here"),
            new("src/B.cs", 1, CommentSeverity.Warning, "Use IDisposable pattern here"),
        }.AsReadOnly();

        var result = FindingDeduplicator.CollapseSameFileDuplicates(comments);

        Assert.Equal(2, result.Count);
    }

    // T008 — Different severities are never merged even if messages are identical
    [Fact]
    public void Deduplicate_DifferentSeverities_NotMerged()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Missing null check on parameter"),
            new("src/B.cs", 2, CommentSeverity.Error, "Missing null check on parameter"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Equal(2, result.Count);
    }

    // T008 — Comments on same file → NOT merged
    [Fact]
    public void Deduplicate_SameFileDuplicates_NotMerged()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Use IDisposable pattern here"),
            new("src/A.cs", 5, CommentSeverity.Warning, "Use IDisposable pattern here"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Equal(2, result.Count);
    }

    // T008 — Comment with null FilePath (PR-level) → never used as merge source
    [Fact]
    public void Deduplicate_NullFilePathComment_NeverMerged()
    {
        var comments = new List<ReviewComment>
        {
            new(null, null, CommentSeverity.Info, "Use IDisposable pattern here"),
            new("src/A.cs", 1, CommentSeverity.Info, "Use IDisposable pattern here"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        // The null-FilePath comment and the file-specific comment must remain separate
        Assert.Equal(2, result.Count);
    }

    // T008 — Single comment → unchanged
    [Fact]
    public void Deduplicate_SingleComment_Unchanged()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Missing null check on parameter"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Single(result);
        Assert.Equal("src/A.cs", result[0].FilePath);
    }

    // T008 — Empty input → empty result
    [Fact]
    public void Deduplicate_EmptyInput_ReturnsEmpty()
    {
        var result = FindingDeduplicator.Deduplicate(new List<ReviewComment>().AsReadOnly());

        Assert.Empty(result);
    }

    // ── T013: Jaccard threshold 0.50 + stop-word filtering ──────────────────────

    // T013 — Tokenize purely stop words → empty token set after filtering
    [Fact]
    public void Tokenize_AllStopWords_ReturnsEmpty()
    {
        var result = FindingDeduplicator.Tokenize("the is a for and to be in of");

        Assert.Empty(result);
    }

    // T013 — Tokenize tokens shorter than 3 chars → excluded
    [Fact]
    public void Tokenize_TokensUnderThreeChars_Excluded()
    {
        // "ab" and "cd" are 2 chars — below the minimum length threshold
        var result = FindingDeduplicator.Tokenize("ab cd");

        Assert.Empty(result);
    }

    // T013 — Domain-token Jaccard between 0.50 and 0.75 → merges at new 0.50 threshold
    [Fact]
    public void Deduplicate_DomainJaccardBetween50And75_MergesAtNewThreshold()
    {
        // After stop-word removal:
        //   A tokens: exception, null, handling, pattern, error   → 5 tokens
        //   B tokens: exception, null, handling, pattern, warning  → 5 tokens
        //   Intersection: 4  |  Union: 6  |  Jaccard ≈ 0.667 (above 0.50, below 0.75)
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "exception null handling pattern error"),
            new("src/B.cs", 2, CommentSeverity.Warning, "exception null handling pattern warning"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Single(result);
        Assert.Null(result[0].FilePath);
    }

    // T013 — Stop words shared between two otherwise-unrelated messages do not cause a merge
    [Fact]
    public void Deduplicate_OnlyStopWordOverlap_NotMerged()
    {
        // Both messages share "the", "should", "be" (stop words).
        // After stop-word filtering, domain tokens are entirely different → Jaccard = 0
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "the value should be validated"),
            new("src/B.cs", 2, CommentSeverity.Warning, "the field must be sanitized"),
        }.AsReadOnly();

        var result = FindingDeduplicator.Deduplicate(comments);

        Assert.Equal(2, result.Count);
    }
}
