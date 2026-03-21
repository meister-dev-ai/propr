using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests that verify ReviewPrompts includes existing PR comment threads
///     in the AI user message for context-aware reviewing.
/// </summary>
public class ReviewPromptsExistingThreadsTests
{
    private static PullRequest CreatePullRequest(IReadOnlyList<PrCommentThread>? threads = null)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile>().AsReadOnly(),
            PrStatus.Active,
            threads);
    }

    [Fact]
    public void BuildUserMessage_PrLevelThread_ShowsPrLevelIndicator()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                2,
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "**AI Review Summary**\n\nOverall LGTM."),
                }.AsReadOnly()),
        };
        var pr = CreatePullRequest(threads);
        var message = ReviewPrompts.BuildUserMessage(pr);

        Assert.Contains("PR-level", message);
    }

    [Fact]
    public void BuildUserMessage_ThreadWithFilePath_IncludesFileAndLine()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Bar.cs",
                10,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: Missing null check."),
                    new("Alice", "Fixed."),
                }.AsReadOnly()),
        };
        var pr = CreatePullRequest(threads);
        var message = ReviewPrompts.BuildUserMessage(pr);

        Assert.Contains("/src/Bar.cs", message);
        Assert.Contains("10", message);
        Assert.Contains("Bot", message);
        Assert.Contains("Alice", message);
    }

    [Fact]
    public void BuildUserMessage_WithExistingThreads_IncludesExistingThreadsSection()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Null ref."),
                }.AsReadOnly()),
        };
        var pr = CreatePullRequest(threads);
        var message = ReviewPrompts.BuildUserMessage(pr);

        Assert.Contains("Existing Review Threads", message);
    }

    [Fact]
    public void BuildUserMessage_WithNoExistingThreads_OmitsExistingThreadsSection()
    {
        var pr = CreatePullRequest([]);
        var message = ReviewPrompts.BuildUserMessage(pr);

        Assert.DoesNotContain("Existing Review Threads", message);
    }

    [Fact]
    public void BuildUserMessage_WithNullExistingThreads_OmitsExistingThreadsSection()
    {
        var pr = CreatePullRequest();
        var message = ReviewPrompts.BuildUserMessage(pr);

        Assert.DoesNotContain("Existing Review Threads", message);
    }
}
