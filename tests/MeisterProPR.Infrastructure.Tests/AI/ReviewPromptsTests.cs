using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests for the per-file and synthesis prompt builders added in feature 015.
/// </summary>
public class ReviewPromptsTests
{
    private static ChangedFile CreateFile(string path, string content = "code", string diff = "+code")
    {
        return new ChangedFile(path, ChangeType.Edit, content, diff);
    }

    private static PrCommentThread CreateThread(string? filePath)
    {
        return new PrCommentThread(
            1,
            filePath,
            filePath is null ? null : 1,
            new List<PrThreadComment>
            {
                new("Author", "comment text", Guid.NewGuid()),
            }.AsReadOnly());
    }

    // T035 — Per-file user message contains file-under-review header
    [Fact]
    public void BuildPerFileUserMessage_ContainsFileHeader()
    {
        var file = CreateFile("src/Foo.cs");
        var allFiles = new List<ChangedFile> { file, CreateFile("src/Bar.cs") }.AsReadOnly();

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 2, allFiles, [], "My PR", "feature/x", "main");

        Assert.Contains("Reviewing file 1 of 2: src/Foo.cs", msg);
        Assert.Contains("[CURRENT FILE]", msg);
    }

    // T035 — Per-file user message contains manifest with all changed files
    [Fact]
    public void BuildPerFileUserMessage_ContainsManifestForAllFiles()
    {
        var file = CreateFile("src/Foo.cs");
        var bar = CreateFile("src/Bar.cs");
        var allFiles = new List<ChangedFile> { file, bar }.AsReadOnly();

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 2, allFiles, [], "My PR", "feature/x", "main");

        Assert.Contains("src/Foo.cs", msg);
        Assert.Contains("src/Bar.cs", msg);
        Assert.Contains("use get_file_content", msg);
    }

    // T035 — Per-file user message only includes filtered threads
    [Fact]
    public void BuildPerFileUserMessage_OnlyRendersFilteredThreads()
    {
        var file = CreateFile("src/Foo.cs");
        var allFiles = new List<ChangedFile> { file }.AsReadOnly();

        var fooThread = CreateThread("src/Foo.cs");
        var barThread = CreateThread("src/Bar.cs");
        var prThread = CreateThread(null);

        // Only fooThread and prThread should be in the message (barThread was pre-filtered out)
        var filteredThreads = new List<PrCommentThread> { fooThread, prThread }.AsReadOnly();

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 1, allFiles, filteredThreads, "My PR", "feature/x", "main");

        Assert.Contains("src/Foo.cs", msg);
        Assert.Contains("(PR-level)", msg);
        // Verify bar thread is NOT present (it was filtered before calling the method)
        Assert.DoesNotContain("src/Bar.cs", msg.Split("--- FILE UNDER REVIEW ---")[0]);
    }

    // T035 — Per-file system prompt contains file framing instruction
    [Fact]
    public void BuildPerFileSystemPrompt_ContainsFileFramingInstruction()
    {
        var prompt = ReviewPrompts.BuildPerFileSystemPrompt(null, "src/Foo.cs", 2, 5);

        Assert.Contains("src/Foo.cs", prompt);
        Assert.Contains("2 of 5", prompt);
        Assert.Contains("get_file_content", prompt);
    }

    // T035 — Synthesis system prompt mentions no tool calls
    [Fact]
    public void BuildSynthesisSystemPrompt_ContainsNoToolsInstruction()
    {
        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt();

        Assert.Contains("Do not call any tools", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plain text", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // T035 — Synthesis user message contains all file summary headers
    [Fact]
    public void BuildSynthesisUserMessage_ContainsAllFileSummaryHeaders()
    {
        var summaries = new List<(string FilePath, string Summary)>
        {
            ("src/Foo.cs", "Found 2 issues"),
            ("src/Bar.cs", "Looks good"),
        }.AsReadOnly();

        var msg = ReviewPrompts.BuildSynthesisUserMessage(summaries, "My PR", null);

        Assert.Contains("## src/Foo.cs", msg);
        Assert.Contains("## src/Bar.cs", msg);
        Assert.Contains("Found 2 issues", msg);
        Assert.Contains("Looks good", msg);
    }

    // T035 — Synthesis user message includes PR title and description
    [Fact]
    public void BuildSynthesisUserMessage_IncludesPrTitleAndDescription()
    {
        var summaries = new List<(string FilePath, string Summary)>
        {
            ("src/Foo.cs", "Summary"),
        }.AsReadOnly();

        var msg = ReviewPrompts.BuildSynthesisUserMessage(summaries, "PR Title", "PR Description text");

        Assert.Contains("PR Title", msg);
        Assert.Contains("PR Description text", msg);
    }
}
