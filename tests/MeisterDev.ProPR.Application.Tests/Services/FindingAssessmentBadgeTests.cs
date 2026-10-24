// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Markdig;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class FindingAssessmentBadgeTests
{
    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo, false)]
    [InlineData(FindingBadgeStyle.Plain, false)]
    [InlineData(FindingBadgeStyle.ShieldsIo, true)]
    [InlineData(FindingBadgeStyle.Plain, true)]
    public void NormalizedCustomLabelsRemainCanonicalAcrossRepeatedReads(FindingBadgeStyle style, bool assessed)
    {
        foreach (var label in new[] { "A \u0001 B", new string('a', 127) + " b", " \u0001 \t \u0002 ", "A\u0001\t\r\nB" })
        {
            var finding = Finding() with
            {
                RiskAssessment = assessed ? Finding().RiskAssessment : null,
                FindingClassification = Finding().FindingClassification! with { CustomTags = [new(Guid.NewGuid(), "custom", label)] },
            };
            var body = ReviewFindingTextFormatter.Format(finding, badgeStyle: style);
            var restored = ReviewFindingTextFormatter.StripAssessment(body);

            Assert.Equal("ERROR: " + finding.Message, restored);
            Assert.Equal(restored, ReviewFindingTextFormatter.StripAssessment(restored));
            Assert.Equal(label, finding.FindingClassification.CustomTags[0].DisplayName);
        }
    }

    [Fact]
    public void DefaultHeaderUsesShieldsBadgesAndPutsTypesAfterAssessment()
    {
        var finding = Finding();
        var body = ReviewFindingTextFormatter.Format(finding);
        var header = body.Split("\n\n", StringSplitOptions.None)[0];

        Assert.Contains("https://img.shields.io/badge/", header);
        Assert.Contains("style=flat-square", header);
        Assert.True(header.IndexOf("Severity", StringComparison.Ordinal) < header.IndexOf("Risk", StringComparison.Ordinal));
        Assert.True(header.IndexOf("Confidence", StringComparison.Ordinal) < header.IndexOf("Logic error", StringComparison.Ordinal));
        Assert.DoesNotContain("Highest assessed priority", header);
        Assert.DoesNotContain("Fix first", header);
        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public void ThreeTypesFollowAllAssessmentsWithoutRelativePriorityText(FindingBadgeStyle style)
    {
        var finding = Finding() with
        {
            FindingClassification = Finding().FindingClassification! with
            {
                CoreSlugs = [FindingTypeTaxonomy.LogicError, FindingTypeTaxonomy.Security, FindingTypeTaxonomy.Performance]
            },
        };
        var body = ReviewFindingTextFormatter.Format(finding, badgeStyle: style);
        var header = body.Split("\n\n", StringSplitOptions.None)[0];
        var labels = new[]
        {
            "Severity: Error", "Risk: Critical", "Impact: Critical", "Likelihood: Likely", "Confidence: Certain",
            "Type: Logic error", "Type: Security", "Type: Performance"
        };
        var positions = labels.Select(label => header.IndexOf(label, StringComparison.Ordinal)).ToArray();

        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.DoesNotContain("Highest assessed priority", header);
        Assert.DoesNotContain("Fix first", header);
        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
        if (style == FindingBadgeStyle.ShieldsIo)
        {
            Assert.Contains("Severity-Error-64748b", header);
            Assert.Contains("Risk-Critical-dc2626", header);
            Assert.Contains("Impact-Critical-64748b", header);
            Assert.Contains("Type-Security-2563eb", header);
        }
        else
        {
            Assert.DoesNotContain("img.shields.io", body);
            Assert.Contains("<code>Type: Performance</code>", Markdown.ToHtml(body));
        }
    }

    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public void BothStylesRestoreAssessedAndUnassessedClaimsWithUnsafeCustomLabels(FindingBadgeStyle style)
    {
        foreach (var assessed in new[] { false, true })
        {
            var finding = Finding() with
            {
                RiskAssessment = assessed ? Finding().RiskAssessment : null,
                FindingClassification = Finding().FindingClassification! with
                {
                    CustomTags = [new(Guid.NewGuid(), "custom", "a-b_c [x](https://evil.test) <script> `code`` 😀\nmore")],
                },
            };
            var body = ReviewFindingTextFormatter.Format(finding, collapseAssessment: true, badgeStyle: style);
            Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
            Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body.ReplaceLineEndings("\r\n")));
            var html = Markdown.ToHtml(body);
            Assert.DoesNotContain("<script>", html);
            if (style == FindingBadgeStyle.Plain)
            {
                Assert.DoesNotContain("![", body);
                Assert.DoesNotContain("img.shields.io", body);
                Assert.Contains("<code>Type: a-b_c", html);
            }
            else
            {
                Assert.Contains("Type-a--b__c%20", body);
                Assert.Contains("%F0%9F%98%80", body);
                Assert.DoesNotContain("src=\"https://evil.test", html);
            }
        }
    }

    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public void InconsistentAssessmentOrModifiedBadgeUrlDoesNotStripTheComment(FindingBadgeStyle style)
    {
        var body = ReviewFindingTextFormatter.Format(Finding(), badgeStyle: style);
        var inconsistent = body.Replace("| Impact | Critical |", "| Impact | High |", StringComparison.Ordinal);
        Assert.Equal(inconsistent, ReviewFindingTextFormatter.StripAssessment(inconsistent));
        var changed = body.Replace("style=flat-square", "style=flat", StringComparison.Ordinal);
        if (style == FindingBadgeStyle.ShieldsIo)
        {
            Assert.Equal(changed, ReviewFindingTextFormatter.StripAssessment(changed));
        }
    }

    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public void TruncationKeepsTheClaimAndCompleteBadgeTokens(FindingBadgeStyle style)
    {
        var finding = Finding() with
        {
            FindingClassification = Finding().FindingClassification! with
            {
                CustomTags = Enumerable.Range(0, 64).Select(index => new FindingCustomTypeAssignment(
                    Guid.NewGuid(), $"custom-{index}", new string('x', 110) + $"{index}`😀")).ToList(),
            },
        };
        var body = ReviewFindingTextFormatter.Format(finding, htmlAssessment: true, badgeStyle: style);
        foreach (var limit in new[] { 180, 700, 1800, 8000 })
        {
            var shortened = ReviewFindingTextFormatter.Truncate(body, limit);
            Assert.True(shortened.Length <= limit);
            Assert.Contains(finding.Message, shortened);
            if (shortened.StartsWith("![", StringComparison.Ordinal))
            {
                var header = shortened.Split("\n\n", StringSplitOptions.None)[0];
                Assert.EndsWith("?style=flat-square)", header);
            }

            Assert.DoesNotContain('\uFFFD', shortened);
        }
    }

    private static ReviewComment Finding() => new("a.cs", 3, CommentSeverity.Error, "The supplied input deletes other tenants' rows.")
    {
        RiskAssessment = new(
            FindingImpact.Critical, FindingLikelihood.Likely, "Tenant data is deleted.", "Every request uses this query.",
            "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete path omits tenant filtering."),
        FindingClassification = new(["logic-error"], [], null, null, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
    };
}
