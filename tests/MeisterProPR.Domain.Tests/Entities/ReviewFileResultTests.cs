using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Tests.Entities;

public class ReviewFileResultTests
{
    [Fact]
    public void Constructor_WithEmptyFilePath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ReviewFileResult(Guid.NewGuid(), string.Empty));
    }

    [Fact]
    public void Constructor_WithWhitespaceFilePath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ReviewFileResult(Guid.NewGuid(), "   "));
    }

    [Fact]
    public void Constructor_WithValidArgs_SetsProperties()
    {
        var jobId = Guid.NewGuid();
        var result = new ReviewFileResult(jobId, "src/Foo.cs");

        Assert.Equal(jobId, result.JobId);
        Assert.Equal("src/Foo.cs", result.FilePath);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.False(result.IsComplete);
        Assert.False(result.IsFailed);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.PerFileSummary);
        Assert.Null(result.Comments);
    }

    [Fact]
    public void MarkCompleted_SetsIsCompleteAndSummary()
    {
        var result = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        var comments = new List<ReviewComment>().AsReadOnly();

        result.MarkCompleted("summary text", comments);

        Assert.True(result.IsComplete);
        Assert.False(result.IsFailed);
        Assert.Equal("summary text", result.PerFileSummary);
        Assert.Same(comments, result.Comments);
    }

    [Fact]
    public void MarkFailed_SetsIsFailedAndErrorMessage()
    {
        var result = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");

        result.MarkFailed("some error");

        Assert.True(result.IsFailed);
        Assert.False(result.IsComplete);
        Assert.Equal("some error", result.ErrorMessage);
    }

    [Fact]
    public void MarkCompleted_OnAlreadyFailed_Throws()
    {
        var result = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        result.MarkFailed("fail");

        Assert.Throws<InvalidOperationException>(() =>
            result.MarkCompleted("summary", new List<ReviewComment>().AsReadOnly()));
    }

    [Fact]
    public void MarkFailed_OnAlreadyCompleted_Throws()
    {
        var result = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        result.MarkCompleted("summary", new List<ReviewComment>().AsReadOnly());

        Assert.Throws<InvalidOperationException>(() => result.MarkFailed("error"));
    }
}
