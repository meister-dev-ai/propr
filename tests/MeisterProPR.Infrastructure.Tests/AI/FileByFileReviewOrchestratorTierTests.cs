// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     T034 — unit tests for <see cref="FileByFileReviewOrchestrator.ClassifyTier" /> and
///     <see cref="FileByFileReviewOrchestrator.CountChangedLines" />.
/// </summary>
public sealed class FileByFileReviewOrchestratorTierTests
{
    // ─── ClassifyTier ────────────────────────────────────────────────────────────

    [Fact]
    public void ClassifyTier_10LineDiff_ReturnsLow()
    {
        var file = BuildFile(BuildDiff(10));
        Assert.Equal(FileComplexityTier.Low, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    [Fact]
    public void ClassifyTier_100LineDiff_ReturnsMedium()
    {
        var file = BuildFile(BuildDiff(100));
        Assert.Equal(FileComplexityTier.Medium, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    [Fact]
    public void ClassifyTier_300LineDiff_ReturnsHigh()
    {
        var file = BuildFile(BuildDiff(300));
        Assert.Equal(FileComplexityTier.High, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    [Fact]
    public void ClassifyTier_NullDiffAndNoFullContent_ReturnsLow()
    {
        var file = BuildFile();
        Assert.Equal(FileComplexityTier.Low, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    [Fact]
    public void ClassifyTier_150LineDiff_ReturnsMedium_BoundaryInclusive()
    {
        // 150 changed lines → boundary is ≤150 → Medium
        var file = BuildFile(BuildDiff(150));
        Assert.Equal(FileComplexityTier.Medium, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    [Fact]
    public void ClassifyTier_30LineDiff_ReturnsLow_BoundaryInclusive()
    {
        // 30 changed lines → boundary is ≤30 → Low
        var file = BuildFile(BuildDiff(30));
        Assert.Equal(FileComplexityTier.Low, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    [Fact]
    public void ClassifyTier_31LineDiff_ReturnsMedium()
    {
        var file = BuildFile(BuildDiff(31));
        Assert.Equal(FileComplexityTier.Medium, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    [Fact]
    public void ClassifyTier_151LineDiff_ReturnsHigh()
    {
        var file = BuildFile(BuildDiff(151));
        Assert.Equal(FileComplexityTier.High, FileByFileReviewOrchestrator.ClassifyTier(file));
    }

    // ─── CountChangedLines ───────────────────────────────────────────────────────

    [Fact]
    public void CountChangedLines_NullDiff_ReturnsZero()
    {
        Assert.Equal(0, FileByFileReviewOrchestrator.CountChangedLines(null));
    }

    [Fact]
    public void CountChangedLines_EmptyDiff_ReturnsZero()
    {
        Assert.Equal(0, FileByFileReviewOrchestrator.CountChangedLines(string.Empty));
    }

    [Fact]
    public void CountChangedLines_CountsPlusAndMinusLines()
    {
        var diff = "+added line\n-removed line\n context line\n+another added";
        Assert.Equal(3, FileByFileReviewOrchestrator.CountChangedLines(diff));
    }

    [Fact]
    public void CountChangedLines_IgnoresDiffHeaders()
    {
        var diff = "@@ -1,3 +1,4 @@\n+new line\n unchanged";
        Assert.Equal(1, FileByFileReviewOrchestrator.CountChangedLines(diff));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static ChangedFile BuildFile(string? diff = null, string? fullContent = null)
    {
        return new ChangedFile(
            "src/Foo.cs",
            ChangeType.Edit,
            fullContent ?? string.Empty,
            diff ?? string.Empty);
    }

    private static string BuildDiff(int changedLines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < changedLines; i++)
        {
            sb.AppendLine($"+line {i}");
        }

        return sb.ToString();
    }
}
