// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Services;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Tests for the bot-author detection helper and deduplication filtering logic
///     in AdoCommentPoster. These tests exercise pure logic without real ADO calls.
/// </summary>
public class AdoCommentPosterDeduplicationTests
{
    // The earlier comment carries other ratings than the current finding, which must not change the finding's identity.
    [Theory]
    [InlineData("shields", FindingBadgeStyle.ShieldsIo)]
    [InlineData("shields", FindingBadgeStyle.Plain)]
    [InlineData("plain", FindingBadgeStyle.ShieldsIo)]
    [InlineData("plain", FindingBadgeStyle.Plain)]
    public void EarlierBadgeCommentAtAdjacentLine_MatchesTheCurrentClaimAcrossStylesAndRatings(string earlierStyle, FindingBadgeStyle style)
    {
        var header = earlierStyle switch
        {
            "plain" => "`Severity: Error` `Risk: High` `Impact: High` `Likelihood: Possible` `Confidence: Certain` `Type: Logic error`",
            _ => "![Severity: Error](https://img.shields.io/badge/Severity-Error-64748b?style=flat-square) "
                 + "![Risk: High](https://img.shields.io/badge/Risk-High-ea580c?style=flat-square) "
                 + "![Impact: High](https://img.shields.io/badge/Impact-High-64748b?style=flat-square) "
                 + "![Likelihood: Possible](https://img.shields.io/badge/Likelihood-Possible-64748b?style=flat-square) "
                 + "![Confidence: Certain](https://img.shields.io/badge/Confidence-Certain-64748b?style=flat-square) "
                 + "![Type: Logic error](https://img.shields.io/badge/Type-Logic%20error-2563eb?style=flat-square)",
        };
        const string claim = "Guard the shared service before use.";
        var earlier = header + "\n\n" + claim + "\n\n<!-- propr-risk-assessment:start -->\n\n"
                      + "<details>\n<summary>Assessment details</summary>\n\n| Assessment | Level | Reason |\n| --- | --- | --- |\n"
                      + "| Risk | High | Derived from impact and likelihood. |\n"
                      + "| Impact | High | The request fails. |\n"
                      + "| Likelihood | Possible | The optional service can be absent. |\n"
                      + "| Confidence | Certain | The dereference precedes validation. |\n\n</details>\n\n<!-- propr-risk-assessment:end -->";
        var composer = TestPostedCommentComposer.Default;
        var current = new ReviewComment("src/Work.cs", 42, CommentSeverity.Error, claim)
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Critical, FindingLikelihood.Likely, "All requests fail.", "Every request.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, "The complete path is visible."),
        };
        var currentBody = AdoCommentPoster.FormatInlineCommentBody(current, composer, badgeStyle: style);
        var restored = ReviewFindingTextFormatter.StripAssessment(composer.StripRendered(currentBody, ReviewFindingTextFormatter.RenderPlainText));
        Assert.Equal("ERROR: " + claim, restored);

        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var priorBody = composer.Append(earlier).ReplaceLineEndings(newline);
            var threads = new List<PrCommentThread> { new("27", "/src/Work.cs", 43, [new PrThreadComment("Bot", priorBody, BotId)]) };
            var deterministic = AdoCommentPoster.FindDeterministicDuplicateMatch(threads, "src/Work.cs", 42, current.Message, BotId, composer);
            var fallback = AdoCommentPoster.FindFallbackDuplicateMatch(threads, "src/Work.cs", 42, current.Message, BotId, composer);

            Assert.Equal("normalized_text_match", deterministic?.ReasonCode);
            Assert.Equal("fallback_duplicate_match", fallback?.ReasonCode);
            Assert.Equal(restored, ReviewFindingTextFormatter.StripAssessment(composer.Strip(priorBody)));
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void HtmlFindingWithRepairedCodeFenceAtAdjacentLine_MatchesTheUnmodifiedRawClaim(string newline)
    {
        const string message = "Literal <!-- propr-fence-repair -->.\n```csharp\nTask<T> result;";
        var finding = new ReviewComment("src/Work.cs", 43, CommentSeverity.Warning, message)
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, "Service fails.", "On retry.", "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };
        var body = AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default).ReplaceLineEndings(newline);
        var threads = new List<PrCommentThread> { new("27", "src/Work.cs", 43, [new PrThreadComment("Bot", body, BotId)]) };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(threads, "src/Work.cs", 42, message, BotId, TestPostedCommentComposer.Default);

        Assert.NotNull(match);
        Assert.Equal("normalized_text_match", match.ReasonCode);
        Assert.Contains("<details><summary>Assessment details</summary><table>", body, StringComparison.Ordinal);
        Assert.Equal(message, finding.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawBoldClassificationHeadingAtAdjacentLine_RemainsPartOfFindingIdentity(bool assessed)
    {
        const string rawMessage = "**Warning**\n\nLeaks secrets.";
        var finding = new ReviewComment("src/Foo.cs", 43, CommentSeverity.Error, rawMessage)
        {
            RiskAssessment = assessed
                ? new FindingRiskAssessment(
                    FindingImpact.High, FindingLikelihood.Possible, "Credentials are exposed.", "On retry.", "review_model",
                    FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, "The path is visible.")
                : null,
        };
        var composer = TestPostedCommentComposer.Default;
        var priorBody = AdoCommentPoster.FormatInlineCommentBody(finding, composer);
        var threads = new List<PrCommentThread> { new("27", "/src/Foo.cs", 43, [new PrThreadComment("Bot", priorBody, BotId)]) };

        var deterministic = AdoCommentPoster.FindDeterministicDuplicateMatch(threads, "src/Foo.cs", 42, rawMessage, BotId, composer);
        var fallback = AdoCommentPoster.FindFallbackDuplicateMatch(threads, "src/Foo.cs", 42, rawMessage, BotId, composer);

        Assert.NotNull(deterministic);
        Assert.Equal("normalized_text_match", deterministic.ReasonCode);
        Assert.NotNull(fallback);
        Assert.Equal("fallback_duplicate_match", fallback.ReasonCode);
        Assert.Contains(rawMessage, priorBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("\n", false, FindingBadgeStyle.Plain)]
    [InlineData("\r\n", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("\r\n", false, FindingBadgeStyle.Plain)]
    [InlineData("\n", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("\n", true, FindingBadgeStyle.Plain)]
    [InlineData("\r\n", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("\r\n", true, FindingBadgeStyle.Plain)]
    public void CompactRemoteFindingAtAdjacentLine_MatchesTheRawClaimAcrossRatingChanges(string newline, bool assessed, FindingBadgeStyle style)
    {
        var composer = TestPostedCommentComposer.With("Generated <em>by ProPR</em>.");
        var finding = new ReviewComment("src/Foo.cs", 43, CommentSeverity.Error, "Guard the shared service before use.")
        {
            FindingClassification = new(
                ["logic-error"], [new(Guid.NewGuid(), "custom", "A \u0001 B")], null, null, null,
                FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
            RiskAssessment = assessed
                ? new FindingRiskAssessment(
                    FindingImpact.High, FindingLikelihood.Possible, "Service fails.", "On retry.", "review_model",
                    FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, "The path is visible.")
                : null,
        };
        var priorBody = AdoCommentPoster.FormatInlineCommentBody(finding, composer, badgeStyle: style).Replace("\n", newline, StringComparison.Ordinal);
        var threads = new List<PrCommentThread> { new("27", "/src/Foo.cs", 43, [new PrThreadComment("Bot", priorBody, BotId)]) };
        var current = finding with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Critical, FindingLikelihood.Likely, "All tenants fail.", "Every request.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "Reproduced."),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(threads, "src/Foo.cs", 42, current.Message, BotId, composer);
        var fallback = AdoCommentPoster.FindFallbackDuplicateMatch(threads, "src/Foo.cs", 42, current.Message, BotId, composer);
        var switchedStyle = style == FindingBadgeStyle.ShieldsIo ? FindingBadgeStyle.Plain : FindingBadgeStyle.ShieldsIo;
        var currentBody = AdoCommentPoster.FormatInlineCommentBody(current, composer, badgeStyle: switchedStyle);

        Assert.StartsWith(style == FindingBadgeStyle.Plain ? "`Severity: Error`" : "![Severity: Error]", priorBody);
        Assert.NotNull(match);
        Assert.NotNull(fallback);
        Assert.Equal("normalized_text_match", match!.ReasonCode);
        Assert.Equal("fallback_duplicate_match", fallback!.ReasonCode);
        Assert.Equal(
            "ERROR: " + current.Message,
            ReviewFindingTextFormatter.StripAssessment(composer.StripRendered(currentBody, ReviewFindingTextFormatter.RenderPlainText)));
    }

    [Fact]
    public void AssessedRemoteFinding_IsMatchedUsingRawFindingText()
    {
        var risk = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Possible, "Service fails.", "A configured retry triggers it.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete path dereferences the null service.");
        var finding = new ReviewComment("src/Foo.cs", 43, CommentSeverity.Error, "Add a null check before dereferencing the service.")
            { RiskAssessment = risk };
        var body = AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default);
        var threads = new List<PrCommentThread> { new("27", "/src/Foo.cs", 43, [new PrThreadComment("Bot", body, BotId)]) };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(threads, "src/Foo.cs", 42, finding.Message, BotId, TestPostedCommentComposer.Default);
        Assert.Equal("normalized_text_match", match!.ReasonCode);
        Assert.Contains("<tr><td>Risk</td><td>High</td>", body);
        Assert.Contains("Service fails.", body);
        Assert.Contains("<tr><td>Confidence</td><td>Certain</td>", body);
        Assert.Equal("Add a null check before dereferencing the service.", finding.Message);
    }

    private static readonly Guid BotId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");


    [Fact]
    public void HasBotSummary_WithExistingSummaryThread_ReturnsTrue()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "1",
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
                "1",
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
                "1",
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
                "1",
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
    public void HasBotSummary_WhenBotIdMissingButPublicationIdentityMatchesAuthorName_ReturnsTrue()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "1",
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Meister Bot", "**AI Review Summary**\n\nLooks good."),
                }.AsReadOnly()),
        };
        var publicationIdentity = new ReviewerIdentity(
            new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org"),
            "meister-bot",
            "meister-bot",
            "Meister Bot",
            true);

        Assert.True(AdoCommentPoster.HasBotSummary(threads, null, publicationIdentity));
    }


    [Fact]
    public void HasBotThreadAt_BotThreadAtSameFileAndLine_ReturnsTrue()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "1",
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
                "1",
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
                "1",
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
                "1",
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
                "1",
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
                "1",
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
                "15",
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
            BotId,
            TestPostedCommentComposer.Default);

        Assert.NotNull(match);
        Assert.Equal("resolved_thread_match", match.ReasonCode);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_FilelessCandidateAgainstBotSummaryThread_DoesNotSuppress()
    {
        // A candidate with no file anchor has nothing to compare on, so it must not match the
        // pull-request-level summary thread just because both anchors are absent.
        var threads = new List<PrCommentThread>
        {
            new(
                "7",
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "**AI Review Summary**\n\nLooks good.", BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            null,
            null,
            "Two callers duplicate the retry policy; extract it into one helper.",
            BotId,
            TestPostedCommentComposer.Default);

        Assert.Null(match);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_FilelessCandidateAgainstResolvedPrLevelThread_DoesNotSuppress()
    {
        // A resolved pull-request-level thread only suppresses a fileless candidate that repeats its
        // content. Its status alone cannot stand in for an anchor, or one closed thread silences every
        // later fileless finding.
        var threads = new List<PrCommentThread>
        {
            new(
                "11",
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: The retry policy is duplicated.", BotId),
                }.AsReadOnly(),
                "Fixed"),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            null,
            null,
            "Configuration is read before validation runs.",
            BotId,
            TestPostedCommentComposer.Default);

        Assert.Null(match);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_FilelessCandidateMatchingPriorPrLevelText_Suppresses()
    {
        // Content equality is still a legitimate suppression for a fileless candidate.
        var threads = new List<PrCommentThread>
        {
            new(
                "8",
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: Two callers duplicate the retry policy.", BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            null,
            null,
            "Two callers duplicate the retry policy",
            BotId,
            TestPostedCommentComposer.Default);

        Assert.NotNull(match);
        Assert.Equal("normalized_text_match", match.ReasonCode);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_InlineCandidateAtSameFileAndLine_Suppresses()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "9",
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Null ref.", BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            "src/Foo.cs",
            42,
            "A different concern entirely.",
            BotId,
            TestPostedCommentComposer.Default);

        Assert.NotNull(match);
        Assert.Equal("normalized_location_match", match.ReasonCode);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_FileLevelCandidateWithoutLine_Suppresses()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "10",
                "/src/Foo.cs",
                null,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: File-level concern.", BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            "src/Foo.cs",
            null,
            "An unrelated file-level concern.",
            BotId,
            TestPostedCommentComposer.Default);

        Assert.NotNull(match);
        Assert.Equal("normalized_location_match", match.ReasonCode);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_NormalizedTextMatch_IgnoresSeverityPrefixAndWhitespace()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "27",
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
            BotId,
            TestPostedCommentComposer.Default);

        Assert.NotNull(match);
        Assert.Equal("normalized_text_match", match.ReasonCode);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_AgainstAFindingPostedBeforeTheMarker_StillSuppresses()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "31",
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
            BotId,
            TestPostedCommentComposer.With("Erzeugt von ProPR."));

        Assert.NotNull(match);
        Assert.Equal("normalized_text_match", match.ReasonCode);
    }

    [Fact]
    public void FindDeterministicDuplicateMatch_AgainstAMarkedFinding_MatchesTheSameConcern()
    {
        var composer = TestPostedCommentComposer.Default;
        var threads = new List<PrCommentThread>
        {
            new(
                "32",
                "/src/Foo.cs",
                43,
                new List<PrThreadComment>
                {
                    new("Bot", composer.Append("ERROR: Add a null check before dereferencing the service."), BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindDeterministicDuplicateMatch(
            threads,
            "src/Foo.cs",
            42,
            "Add a null check before dereferencing the service",
            BotId,
            composer);

        Assert.NotNull(match);
        Assert.Equal("normalized_text_match", match.ReasonCode);
    }

    [Fact]
    public void HasBotSummary_WithAMarkedSummaryThread_ReturnsTrue()
    {
        var composer = TestPostedCommentComposer.Default;
        var threads = new List<PrCommentThread>
        {
            new(
                "1",
                null,
                null,
                new List<PrThreadComment>
                {
                    new("Bot", composer.Append("**AI Review Summary**\n\nLooks good."), BotId),
                }.AsReadOnly()),
        };

        Assert.True(AdoCommentPoster.HasBotSummary(threads, BotId));
    }

    [Fact]
    public void FindFallbackDuplicateMatch_RewordedConcernAboveThreshold_ReturnsFallbackReason()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "33",
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new(
                        "Bot",
                        "WARNING: Validate the configuration value before using it as a connection string.",
                        BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindFallbackDuplicateMatch(
            threads,
            "/src/Foo.cs",
            42,
            "Validate the config value before using it as the connection string.",
            BotId,
            TestPostedCommentComposer.Default);

        Assert.NotNull(match);
        Assert.Equal("fallback_duplicate_match", match.ReasonCode);
    }

    [Fact]
    public void FindFallbackDuplicateMatch_DifferentConcern_DoesNotSuppress()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "34",
                "/src/Foo.cs",
                42,
                new List<PrThreadComment>
                {
                    new(
                        "Bot",
                        "WARNING: Validate the configuration value before using it as a connection string.",
                        BotId),
                }.AsReadOnly()),
        };

        var match = AdoCommentPoster.FindFallbackDuplicateMatch(
            threads,
            "/src/Foo.cs",
            42,
            "Dispose the HttpClient created in this helper to avoid socket exhaustion.",
            BotId,
            TestPostedCommentComposer.Default);

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


    [Fact]
    public void BuildChangeTrackingIds_DuplicatePathAcrossChanges_DoesNotThrowAndCollapses()
    {
        var changes = new List<GitPullRequestChange>
        {
            Change("/src/Foo.cs", 11, VersionControlChangeType.Edit),
            Change("/src/Foo.cs", 22, VersionControlChangeType.Edit),
        };

        var map = AdoCommentPoster.BuildChangeTrackingIds(changes);

        Assert.Single(map);
        Assert.Equal(11, map["/src/Foo.cs"]);
    }

    [Fact]
    public void BuildChangeTrackingIds_DuplicatePathWithDelete_PrefersNonDeleteChange()
    {
        var changes = new List<GitPullRequestChange>
        {
            Change("/src/Foo.cs", 5, VersionControlChangeType.Delete),
            Change("/src/Foo.cs", 6, VersionControlChangeType.Edit),
        };

        var map = AdoCommentPoster.BuildChangeTrackingIds(changes);

        Assert.Equal(6, map["/src/Foo.cs"]);
    }

    [Fact]
    public void BuildChangeTrackingIds_NormalizesBackslashesAndLeadingSlash()
    {
        var changes = new List<GitPullRequestChange>
        {
            Change("src\\Foo.cs", 7, VersionControlChangeType.Edit),
        };

        var map = AdoCommentPoster.BuildChangeTrackingIds(changes);

        Assert.Equal(7, map["/src/Foo.cs"]);
    }

    [Fact]
    public void BuildChangeTrackingIds_SkipsChangesWithoutItemPath()
    {
        var changes = new List<GitPullRequestChange>
        {
            new() { ChangeTrackingId = 1, ChangeType = VersionControlChangeType.Edit, Item = null },
            Change("/src/Bar.cs", 9, VersionControlChangeType.Edit),
        };

        var map = AdoCommentPoster.BuildChangeTrackingIds(changes);

        Assert.Single(map);
        Assert.Equal(9, map["/src/Bar.cs"]);
    }

    [Fact]
    public void FindBotSummaryThreadId_ReturnsTheFirstSummaryThreadWithAnIdThatReadsAsANumber()
    {
        // A refusal is posted as a reply into the bot's summary thread. Stopping at the first summary thread
        // and giving up on an id that does not parse leaves a usable thread behind and posts nothing.
        var threads = new List<PrCommentThread>
        {
            new(
                "not-a-number",
                null,
                null,
                new List<PrThreadComment> { new("Bot", "**AI Review Summary**\n\nFirst.", BotId) }.AsReadOnly()),
            new(
                "42",
                null,
                null,
                new List<PrThreadComment> { new("Bot", "**AI Review Summary**\n\nSecond.", BotId) }.AsReadOnly()),
        };

        Assert.Equal(42, AdoCommentPoster.FindBotSummaryThreadId(threads, BotId));
    }

    [Fact]
    public void FindBotSummaryThreadId_WithoutABotSummaryThread_ReturnsNull()
    {
        var threads = new List<PrCommentThread>
        {
            new(
                "7",
                null,
                null,
                new List<PrThreadComment> { new("Someone", "**AI Review Summary**\n\nNot ours.", UserId) }.AsReadOnly()),
        };

        Assert.Null(AdoCommentPoster.FindBotSummaryThreadId(threads, BotId));
    }

    private static GitPullRequestChange Change(string path, int trackingId, VersionControlChangeType changeType)
    {
        return new GitPullRequestChange
        {
            ChangeTrackingId = trackingId,
            ChangeType = changeType,
            Item = new GitItem { Path = path },
        };
    }
}
