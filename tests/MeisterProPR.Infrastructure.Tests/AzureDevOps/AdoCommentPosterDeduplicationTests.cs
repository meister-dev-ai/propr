// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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

    [Fact]
    public void HasBotThreadAt_PathWithoutLeadingSlash_MatchesNormalizedThreadPath()
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

        Assert.True(AdoCommentPoster.HasBotThreadAt(threads, "src/Foo.cs", 42, BotId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void HasBotThreadAt_ZeroAndNullAnchors_AreEquivalent(int? candidateLine)
    {
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Foo.cs",
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: File-level concern.", BotId),
                }.AsReadOnly()),
        };

        Assert.True(AdoCommentPoster.HasBotThreadAt(threads, "/src/Foo.cs", candidateLine, BotId));
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_ResolvedThreadAtSameAnchor_UsesResolvedReason()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                15,
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Null ref.", BotId),
                }.AsReadOnly(),
                "Fixed"),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            "/src/Foo.cs",
            42,
            "Null ref.",
            BotId);

        Assert.NotNull(match);
        Assert.Equal("resolved_thread_match", match.ReasonCode);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_NormalizedTextMatch_IgnoresSeverityPrefixAndWhitespace()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                27,
                "/src/Foo.cs",
                43,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Add a null check before dereferencing the service.", BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            "src/Foo.cs",
            42,
            "Add a null check before dereferencing the service",
            BotId);

        Assert.NotNull(match);
        Assert.Equal("normalized_text_match", match.ReasonCode);
    }

    [Fact]
    public void FindFallbackDuplicateMatch_RewordedConcernAboveThreshold_ReturnsFallbackReason()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                33,
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: Validate the configuration value before using it as a connection string.", BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindFallbackDuplicateMatch(
            threads,
            "/src/Foo.cs",
            42,
            "Validate the config value before using it as the connection string.",
            BotId);

        Assert.NotNull(match);
        Assert.Equal("fallback_duplicate_match", match.ReasonCode);
    }

    [Fact]
    public void FindFallbackDuplicateMatch_DifferentConcern_DoesNotSuppress()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                34,
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: Validate the configuration value before using it as a connection string.", BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindFallbackDuplicateMatch(
            threads,
            "/src/Foo.cs",
            42,
            "Dispose the HttpClient created in this helper to avoid socket exhaustion.",
            BotId);

        Assert.Null(match);
    }


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
