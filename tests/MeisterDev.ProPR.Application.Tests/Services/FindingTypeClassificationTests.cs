// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class FindingTypeClassificationTests
{
    [Theory]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void TypedUnassessedHeadersRestoreTheRawClaimAcrossLineEndings(string newline)
    {
        var finding = new ReviewComment("a.cs", 3, CommentSeverity.Error, "Keep the exact original claim.")
        {
            FindingClassification = Classified("logic-error"),
        };
        var body = ReviewFindingTextFormatter.Format(finding).ReplaceLineEndings(newline);

        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"types\":[\"invented-type\"]}")]
    [InlineData("{\"types\":false}")]
    public void InvalidClassificationDoesNotSupplyInventedTypes(string value)
    {
        using var document = JsonDocument.Parse("{\"finding_classification\":" + value + "}");
        Assert.Null(FindingTypeClassificationParser.ParseFinding(document.RootElement));
    }

    [Fact]
    public void CustomTagsResolveOnlyFromCapturedVocabularyAndIgnoreModelIdentity()
    {
        var id = Guid.NewGuid();
        var vocabulary = new FindingTypeVocabulary(1, [new(id, "tenant-boundary", "Tenant boundary", "Cross-tenant access.")]);
        using var document = JsonDocument.Parse(
            """
            {"finding_classification":{"types":[" LOGIC-ERROR ","logic-error","tenant-boundary","unknown"],"custom_tag_ids":["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"],
            "level":"cross_file","qualifier":"incorrect","confidence":0.94,"source":"forged","taxonomy_version":99}}
            """);
        var classification = FindingTypeClassificationParser.ParseFinding(document.RootElement, vocabulary)!;
        Assert.Equal(["logic-error"], classification.CoreSlugs);
        Assert.Equal(id, Assert.Single(classification.CustomTags).Id);
        Assert.Equal("Tenant boundary", classification.CustomTags[0].DisplayName);
        Assert.Equal(CodeInsightFindingLevel.CrossFile, classification.Level);
        Assert.True(classification.IsComplete);
        Assert.Equal(FindingTypeClassification.ReviewSource, classification.Source);
        Assert.Equal(1, classification.TaxonomyVersion);
        Assert.Empty(FindingTypeClassificationParser.ParseFinding(document.RootElement)!.CustomTags);
    }

    [Theory]
    [InlineData("\"confidence\":2")]
    [InlineData("\"confidence\":\"0.9\"")]
    [InlineData("\"level\":\"unsupported\"")]
    [InlineData("\"qualifier\":false")]
    public void PartialAxesRetainSupportedTypesWithoutClaimingACompleteClassification(string invalid)
    {
        using var document = JsonDocument.Parse("{\"finding_classification\":{\"types\":[\"logic-error\"]," + invalid + "}}");
        var classification = FindingTypeClassificationParser.ParseFinding(document.RootElement)!;
        Assert.Equal(["logic-error"], classification.CoreSlugs);
        Assert.False(classification.IsComplete);
    }

    [Fact]
    public async Task VocabularyFailureAndMissingProviderRetainCoreClassification()
    {
        var provider = Substitute.For<IFindingTypeVocabularyProvider>();
        provider.GetVocabularyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("unavailable"));
        var loaded = await FindingTypeVocabularyLoader.LoadAsync(provider, Guid.NewGuid(), NullLogger.Instance, default);
        Assert.Same(FindingTypeVocabulary.Core, loaded);
        Assert.Same(FindingTypeVocabulary.Core, await FindingTypeVocabularyLoader.LoadAsync(null, Guid.NewGuid(), NullLogger.Instance, default));
    }

    [Fact]
    public void JsonAndCopiesRetainClassificationRiskAndFix()
    {
        var typed = Classified("logic-error");
        var original = new ReviewComment("a.cs", 4, CommentSeverity.Error, "A zero count fails.")
        {
            FindingClassification = typed,
            RiskAssessment = new FindingRiskAssessment(FindingImpact.Low, FindingLikelihood.Unlikely, "Bounded.", "Rare.", "review_model", "finding-risk-v1"),
            SuggestedFix = new ReviewSuggestedFix(4, 4, "count", "validCount"),
        };
        var restored = JsonSerializer.Deserialize<ReviewComment>(JsonSerializer.Serialize(original))!;
        Assert.Equal(typed.CoreSlugs, restored.FindingClassification!.CoreSlugs);
        Assert.Equal(typed.Confidence, restored.FindingClassification.Confidence);
        Assert.Equal(original.RiskAssessment, restored.RiskAssessment);
        Assert.Equal(original.SuggestedFix, restored.SuggestedFix);
        Assert.Same(typed, original.AsPullRequestLevel(original.Message).FindingClassification);
        Assert.Same(typed, original.WithAnchorAndSeverity("b.cs", 8, CommentSeverity.Error).FindingClassification);
        var context = new ReviewSystemContext(null, [], null) { FindingTypeVocabulary = new(1, [new(Guid.NewGuid(), "custom", "Custom", "Definition")]) };
        Assert.Same(context.FindingTypeVocabulary, context.CloneForPass().FindingTypeVocabulary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedHeadersRestoreTheClaimWithAndWithoutRisk(bool assessed)
    {
        var classification = Classified("concurrency") with { CustomTags = [new(Guid.NewGuid(), "custom", "<details> [link](javascript:x)\n**extra**")] };
        var finding = new ReviewComment("a.cs", 3, CommentSeverity.Error, "Keep the exact original claim.") { FindingClassification = classification };
        if (assessed)
        {
            finding = finding with
            {
                RiskAssessment = new(FindingImpact.High, FindingLikelihood.Possible, "Lost state", "Parallel calls", "review_model", "finding-risk-v1")
            };
        }

        var body = ReviewFindingTextFormatter.Format(finding, htmlAssessment: assessed);
        Assert.Contains("Type: Concurrency", body);
        Assert.DoesNotContain("<details> [link]", body);
        Assert.DoesNotContain("\n**extra**", body);
        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("[\"logic-error\",null,\"invented\"]", "[null]")]
    public void MalformedDurableListsDoNotPreventPublicationOrConsolidation(string cores, string custom)
    {
        var classification = JsonSerializer.Deserialize<FindingTypeClassification>("{\"CoreSlugs\":" + cores + ",\"CustomTags\":" + custom + "}")!;
        var finding = new ReviewComment("a.cs", 3, CommentSeverity.Error, "Keep this finding.") { FindingClassification = classification };
        var body = ReviewFindingTextFormatter.Format(finding);
        Assert.Contains(finding.Message, body);
        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
        var merged = FindingTypeClassification.Merge([classification, Classified("concurrency")])!;
        Assert.Contains("concurrency", merged.CoreSlugs);
        Assert.DoesNotContain(merged.CoreSlugs, slug => !FindingTypeTaxonomy.IsCoreSlug(slug));
        Assert.Empty(merged.CustomTags);
    }

    [Fact]
    public void LongTypeLabelsRetainUnicodeScalarBoundaries()
    {
        var label = new string('a', 127) + "😀" + "ignored";
        var typed = Classified("logic-error") with { CustomTags = [new(Guid.NewGuid(), "custom", label)] };
        var body = ReviewFindingTextFormatter.Format(new ReviewComment(null, null, CommentSeverity.Error, "Claim.") { FindingClassification = typed });
        Assert.Contains(new string('a', 127) + "😀", body);
        Assert.DoesNotContain("ignored", body);
        Assert.DoesNotContain('\uFFFD', body);
    }

    [Fact]
    public void CrossFileDedupRetainsSupportedSourceTypesWithoutChangingSeverity()
    {
        var first = new ReviewComment("a.cs", 1, CommentSeverity.Error, "The tenant cache lookup ignores the tenant identity.")
            { FindingClassification = Classified("logic-error") };
        var second = new ReviewComment("b.cs", 1, CommentSeverity.Error, first.Message) { FindingClassification = Classified("concurrency") };
        var merged = Assert.Single(FindingDeduplicator.Deduplicate([first, second]));
        Assert.Equal(CommentSeverity.Error, merged.Severity);
        Assert.Equal(["logic-error", "concurrency"], merged.FindingClassification!.CoreSlugs);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("semantic-comments")]
    [InlineData("semantic-candidates")]
    public async Task SameFileDedupRetainsTypesFromEveryMergedSource(string engine)
    {
        var first = new ReviewComment("a.cs", 1, CommentSeverity.Error, "The tenant cache lookup ignores the tenant identity.")
            { FindingClassification = Classified("logic-error") };
        var second = new ReviewComment("a.cs", 1, CommentSeverity.Error, first.Message) { FindingClassification = Classified("concurrency") };
        FindingTypeClassification? retained;
        if (engine == "token")
        {
            var merges = new List<FindingMergeRecord>();
            var comment = Assert.Single(FindingDeduplicator.CollapseSameFileDuplicates([first, second], merges));
            Assert.Same(comment, Assert.Single(merges).Kept[0]);
            retained = comment.FindingClassification;
        }
        else
        {
            var judge = Substitute.For<IFindingMergeJudge>();
            judge.AreSameDefectClassAsync(Arg.Any<CandidateReviewFinding>(), Arg.Any<CandidateReviewFinding>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(true);
            var sut = new SemanticFindingDeduplicator(judge);
            if (engine == "semantic-comments")
            {
                var result = await sut.DeduplicateAsync([first, second], Guid.NewGuid());
                var comment = Assert.Single(result.Comments);
                Assert.Same(comment, Assert.Single(result.Merges).Kept[0]);
                retained = comment.FindingClassification;
            }
            else
            {
                var source = new CandidateReviewFinding(
                    "first", new("per_file_comment", "review"), CommentSeverity.Error, first.Message,
                    CandidateReviewFinding.PerFileCommentCategory, "a.cs", 1) { FindingClassification = first.FindingClassification };
                var other = new CandidateReviewFinding(
                    "second", source.Provenance, CommentSeverity.Error, second.Message,
                    CandidateReviewFinding.PerFileCommentCategory, "a.cs", 1) { FindingClassification = second.FindingClassification };
                retained = Assert.Single(await sut.DeduplicateFindingsAsync([source, other], Guid.NewGuid())).FindingClassification;
            }
        }

        Assert.Equal(["logic-error", "concurrency"], retained!.CoreSlugs);
    }

    private static FindingTypeClassification Classified(string slug) => new(
        [slug], [], CodeInsightFindingLevel.Member,
        CodeInsightFindingQualifier.Incorrect, 0.9, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource);
}
