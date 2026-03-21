using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Tests.ValueObjects;

public class PrCommentThreadTests
{
    [Fact]
    public void PrCommentThread_WithFilePath_ReturnsCorrectProperties()
    {
        var comments = new List<PrThreadComment> { new("Alice", "LGTM") }.AsReadOnly();
        var thread = new PrCommentThread(1, "/src/Foo.cs", 42, comments);

        Assert.Equal(1, thread.ThreadId);
        Assert.Equal("/src/Foo.cs", thread.FilePath);
        Assert.Equal(42, thread.LineNumber);
        Assert.Single(thread.Comments);
    }

    [Fact]
    public void PrCommentThread_WithMultipleComments_PreservesOrder()
    {
        var comments = new List<PrThreadComment>
        {
            new("Bot", "ERROR: null ref"),
            new("Alice", "Fixed in this commit."),
        }.AsReadOnly();

        var thread = new PrCommentThread(3, "/src/Bar.cs", 10, comments);

        Assert.Equal(2, thread.Comments.Count);
        Assert.Equal("Bot", thread.Comments[0].AuthorName);
        Assert.Equal("Alice", thread.Comments[1].AuthorName);
    }

    [Fact]
    public void PrCommentThread_WithoutFilePath_IsNullFilePath()
    {
        var thread = new PrCommentThread(2, null, null, new List<PrThreadComment>().AsReadOnly());

        Assert.Null(thread.FilePath);
        Assert.Null(thread.LineNumber);
    }

    [Fact]
    public void PrThreadComment_StoresAuthorAndContent()
    {
        var comment = new PrThreadComment("Alice", "Looks good to me.");

        Assert.Equal("Alice", comment.AuthorName);
        Assert.Equal("Looks good to me.", comment.Content);
    }
}
