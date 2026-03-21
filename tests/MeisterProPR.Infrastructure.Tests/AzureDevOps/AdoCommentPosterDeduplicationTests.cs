using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AzureDevOps;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Tests for the bot-author detection helper and deduplication filtering logic
///     in AdoCommentPoster. These tests exercise pure logic without real ADO calls.
/// </summary>
public class AdoCommentPosterDeduplicationTests
{
    private static readonly Guid BotId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ── HasBotSummary ─────────────────────────────────────────────────────────

    [Fact]
    public void HasBotSummary_WithExistingSummaryThread_ReturnsTrue()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "**AI Review Summary**\n\nLooks good.", BotId),
                }.AsReadOnly()),
        };

        Assert.True(AdoCommentPoster.HasBotSummary(threads, BotId));
    }

    [Fact]
    public void HasBotSummary_WithNoThreads_ReturnsFalse()
    {
        Assert.False(AdoCommentPoster.HasBotSummary([], BotId));
    }

    [Fact]
    public void HasBotSummary_WithOnlyInlineThreads_ReturnsFalse()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Foo.cs",
                5,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Null ref.", BotId),
                }.AsReadOnly()),
        };

        Assert.False(AdoCommentPoster.HasBotSummary(threads, BotId));
    }

    [Fact]
    public void HasBotSummary_BotPrLevelThreadWithNonSummaryContent_ReturnsFalse()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "Review skipped: no changed files.", BotId),
                }.AsReadOnly()),
        };

        Assert.False(AdoCommentPoster.HasBotSummary(threads, BotId));
    }

    [Fact]
    public void HasBotSummary_NullBotId_ReturnsFalse()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "**AI Review Summary**", BotId),
                }.AsReadOnly()),
        };

        Assert.False(AdoCommentPoster.HasBotSummary(threads, null));
    }

    // ── HasBotThreadAt ────────────────────────────────────────────────────────

    [Fact]
    public void HasBotThreadAt_BotThreadAtSameFileAndLine_ReturnsTrue()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Null ref.", BotId),
                }.AsReadOnly()),
        };

        Assert.True(AdoCommentPoster.HasBotThreadAt(threads, "/src/Foo.cs", 42, BotId));
    }

    [Fact]
    public void HasBotThreadAt_BotThreadAtDifferentLine_ReturnsFalse()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Foo.cs",
                99,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Different line.", BotId),
                }.AsReadOnly()),
        };

        Assert.False(AdoCommentPoster.HasBotThreadAt(threads, "/src/Foo.cs", 42, BotId));
    }

    [Fact]
    public void HasBotThreadAt_NoExistingThreads_ReturnsFalse()
    {
        Assert.False(AdoCommentPoster.HasBotThreadAt([], "/src/Foo.cs", 42, BotId));
    }

    [Fact]
    public void HasBotThreadAt_NullFilePath_ReturnsFalse()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "**AI Review Summary**", BotId),
                }.AsReadOnly()),
        };

        Assert.False(AdoCommentPoster.HasBotThreadAt(threads, null, null, BotId));
    }

    [Fact]
    public void HasBotThreadAt_UserThreadAtSameLocation_ReturnsFalse()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new("Alice", "Fixed this.", UserId),
                }.AsReadOnly()),
        };

        Assert.False(AdoCommentPoster.HasBotThreadAt(threads, "/src/Foo.cs", 42, BotId));
    }

    // ── IsBotAuthor ───────────────────────────────────────────────────────────

    [Fact]
    public void IsBotAuthor_MatchingGuids_ReturnsTrue()
    {
        Assert.True(AdoCommentPoster.IsBotAuthor(BotId, BotId));
    }

    [Fact]
    public void IsBotAuthor_DifferentGuids_ReturnsFalse()
    {
        Assert.False(AdoCommentPoster.IsBotAuthor(UserId, BotId));
    }

    [Fact]
    public void IsBotAuthor_NullAuthorId_ReturnsFalse()
    {
        Assert.False(AdoCommentPoster.IsBotAuthor(null, BotId));
    }

    [Fact]
    public void IsBotAuthor_NullBotId_ReturnsFalse()
    {
        Assert.False(AdoCommentPoster.IsBotAuthor(BotId, null));
    }

    [Fact]
    public void IsBotAuthor_BothNull_ReturnsFalse()
    {
        Assert.False(AdoCommentPoster.IsBotAuthor(null, null));
    }
}
