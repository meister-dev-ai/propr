using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

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
}