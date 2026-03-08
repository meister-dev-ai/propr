using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Tests.ValueObjects;

public class ValueObjectTests
{
    [Fact]
    public void ChangedFile_AllChangeTypes_Accepted()
    {
        foreach (var changeType in Enum.GetValues<ChangeType>())
        {
            var file = new ChangedFile("f.cs", changeType, "", "");
            Assert.Equal(changeType, file.ChangeType);
        }
    }

    [Fact]
    public void ChangedFile_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChangedFile("", ChangeType.Add, "content", "diff"));
    }

    [Fact]
    public void ChangedFile_NullFullContent_TreatedAsEmpty()
    {
        var file = new ChangedFile("file.cs", ChangeType.Add, null!, "diff");
        Assert.Equal("", file.FullContent);
    }

    [Fact]
    public void ChangedFile_NullPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChangedFile(null!, ChangeType.Add, "content", "diff"));
    }

    [Fact]
    public void ChangedFile_NullUnifiedDiff_TreatedAsEmpty()
    {
        var file = new ChangedFile("file.cs", ChangeType.Add, "content", null!);
        Assert.Equal("", file.UnifiedDiff);
    }

    // ChangedFile tests
    [Fact]
    public void ChangedFile_WithValidPath_Constructs()
    {
        var file = new ChangedFile("src/MyFile.cs", ChangeType.Edit, "content", "diff");
        Assert.Equal("src/MyFile.cs", file.Path);
        Assert.Equal(ChangeType.Edit, file.ChangeType);
    }

    // ClientRegistration tests
    [Fact]
    public void ClientRegistration_Constructs()
    {
        var reg = new ClientRegistration("my-key");
        Assert.Equal("my-key", reg.Key);
    }

    // PullRequest tests
    [Fact]
    public void PullRequest_Constructs()
    {
        var files = new List<ChangedFile>().AsReadOnly();
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "My PR",
            "desc",
            "feature/x",
            "main",
            files);
        Assert.Equal("My PR", pr.Title);
        Assert.Equal("desc", pr.Description);
        Assert.Empty(pr.ChangedFiles);
    }

    [Fact]
    public void ReviewComment_AllSeverityLevelsAccepted()
    {
        foreach (var severity in Enum.GetValues<CommentSeverity>())
        {
            var comment = new ReviewComment(null, null, severity, "msg");
            Assert.Equal(severity, comment.Severity);
        }
    }

    [Fact]
    public void ReviewComment_EmptyMessage_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new ReviewComment(null, null, CommentSeverity.Info, ""));
    }

    [Fact]
    public void ReviewComment_FilePathCanBeNull()
    {
        var comment = new ReviewComment(null, null, CommentSeverity.Warning, "msg");
        Assert.Null(comment.FilePath);
    }

    [Fact]
    public void ReviewComment_LineNumberCanBeNull()
    {
        var comment = new ReviewComment("file.cs", null, CommentSeverity.Warning, "msg");
        Assert.Null(comment.LineNumber);
    }

    [Fact]
    public void ReviewComment_NullMessage_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new ReviewComment(null, null, CommentSeverity.Info, null!));
    }

    // ReviewComment tests
    [Fact]
    public void ReviewComment_WithValidMessageAndSeverity_Constructs()
    {
        var comment = new ReviewComment(null, null, CommentSeverity.Info, "Some message");
        Assert.Equal(CommentSeverity.Info, comment.Severity);
        Assert.Equal("Some message", comment.Message);
    }

    // ReviewResult tests
    [Fact]
    public void ReviewResult_Constructs()
    {
        var comments = new List<ReviewComment> { new(null, null, CommentSeverity.Info, "msg") };
        var result = new ReviewResult("summary", comments.AsReadOnly());
        Assert.Equal("summary", result.Summary);
        Assert.Single(result.Comments);
    }
}