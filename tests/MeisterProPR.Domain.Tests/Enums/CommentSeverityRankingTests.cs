// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Enums;

/// <summary>
///     Tests for the product severity ranking used by the minimum-severity-to-post threshold. The ranking is
///     deliberately independent of the enum's declaration order.
/// </summary>
public class CommentSeverityRankingTests
{
    [Fact]
    public void Rank_OrdersErrorAboveWarningAboveSuggestionAboveInfo()
    {
        Assert.True(CommentSeverity.Error.Rank() > CommentSeverity.Warning.Rank());
        Assert.True(CommentSeverity.Warning.Rank() > CommentSeverity.Suggestion.Rank());
        Assert.True(CommentSeverity.Suggestion.Rank() > CommentSeverity.Info.Rank());
    }

    [Theory]
    [InlineData(CommentSeverity.Info, 0)]
    [InlineData(CommentSeverity.Suggestion, 1)]
    [InlineData(CommentSeverity.Warning, 2)]
    [InlineData(CommentSeverity.Error, 3)]
    public void Rank_ReturnsExpectedValue(CommentSeverity severity, int expectedRank)
    {
        Assert.Equal(expectedRank, severity.Rank());
    }

    [Fact]
    public void MeetsMinimum_InfoThreshold_AdmitsEverySeverity()
    {
        foreach (var severity in Enum.GetValues<CommentSeverity>())
        {
            Assert.True(severity.MeetsMinimum(CommentSeverity.Info));
        }
    }

    [Theory]
    // Threshold = Warning: Warning and Error post; Suggestion and Info do not.
    [InlineData(CommentSeverity.Error, CommentSeverity.Warning, true)]
    [InlineData(CommentSeverity.Warning, CommentSeverity.Warning, true)]
    [InlineData(CommentSeverity.Suggestion, CommentSeverity.Warning, false)]
    [InlineData(CommentSeverity.Info, CommentSeverity.Warning, false)]
    // Threshold = Error: only Error posts.
    [InlineData(CommentSeverity.Error, CommentSeverity.Error, true)]
    [InlineData(CommentSeverity.Warning, CommentSeverity.Error, false)]
    // Threshold = Suggestion: Suggestion, Warning, and Error post; Info does not.
    [InlineData(CommentSeverity.Suggestion, CommentSeverity.Suggestion, true)]
    [InlineData(CommentSeverity.Info, CommentSeverity.Suggestion, false)]
    public void MeetsMinimum_ComparesByRank(CommentSeverity severity, CommentSeverity minimum, bool expected)
    {
        Assert.Equal(expected, severity.MeetsMinimum(minimum));
    }

    [Fact]
    public void MeetsMinimum_IsIndependentOfEnumDeclarationOrder()
    {
        // The enum declares Info, Warning, Error, Suggestion — Suggestion has the highest ordinal but a rank below
        // Warning. A Warning threshold must therefore NOT admit a Suggestion despite its larger ordinal value.
        Assert.True((int)CommentSeverity.Suggestion > (int)CommentSeverity.Warning);
        Assert.False(CommentSeverity.Suggestion.MeetsMinimum(CommentSeverity.Warning));
    }
}
