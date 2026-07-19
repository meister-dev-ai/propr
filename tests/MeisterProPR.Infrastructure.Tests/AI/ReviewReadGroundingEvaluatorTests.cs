// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewReadGroundingEvaluatorTests
{
    private static string Content(int lines)
    {
        return string.Join("\n", Enumerable.Range(1, lines).Select(i => $"code line {i}"));
    }

    private static ReviewCommentReadGrounding? Classify(string? filePath, int? lineNumber, params FileReadRecord[] reads)
    {
        return ReviewReadGroundingEvaluator.Classify(filePath, lineNumber, reads);
    }

    // --- Classify: pure typed comparison over captured reads ---

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("src/Foo.cs", null)]
    [InlineData("src/Foo.cs", 0)]
    [InlineData("src/Foo.cs", -3)]
    public void Classify_WhenGroundingDoesNotApply_ReturnsNull(string? filePath, int? lineNumber)
    {
        Assert.Null(Classify(filePath, lineNumber, new FileReadRecord("src/Foo.cs", 1, 50, HasContent: true, 50)));
    }

    [Fact]
    public void Classify_WhenCoveringReadReachesTheCitedLine_ReturnsCovered()
    {
        Assert.Equal(
            ReviewCommentReadGrounding.Covered,
            Classify("src/Foo.cs", 50, new FileReadRecord("src/Foo.cs", 1, 100, HasContent: true, 60)));
    }

    [Fact]
    public void Classify_NormalizesFindingPathSeparatorsAndLeadingSlash()
    {
        Assert.Equal(
            ReviewCommentReadGrounding.Covered,
            Classify("/src\\Foo.cs", 10, new FileReadRecord("src/Foo.cs", 1, 50, HasContent: true, 50)));
    }

    [Fact]
    public void Classify_WhenNoRequestedWindowCoversTheCitedLine_ReturnsNotRead()
    {
        Assert.Equal(
            ReviewCommentReadGrounding.NotRead,
            Classify(
                "src/Foo.cs",
                40,
                new FileReadRecord("src/Other.cs", 1, 100, HasContent: true, 100), // different file
                new FileReadRecord("src/Foo.cs", 1, 20, HasContent: true, 20))); // window ends before the cited line
    }

    [Fact]
    public void Classify_WhenCoveringReadHadNoContent_ReturnsNotReadRatherThanContradicted()
    {
        // An empty / unavailable read is ambiguous (missing file, transient failure, binary): it must never
        // discard a finding — the conservative verdict is "not read".
        Assert.Equal(
            ReviewCommentReadGrounding.NotRead,
            Classify("src/Foo.cs", 50, new FileReadRecord("src/Foo.cs", 1, 100, HasContent: false, 0)));
    }

    [Fact]
    public void Classify_WhenRequestedWindowCoversLineButContentEndedBefore_ReturnsCitedLineMissing()
    {
        Assert.Equal(
            ReviewCommentReadGrounding.CitedLineMissing,
            Classify("src/Foo.cs", 50, new FileReadRecord("src/Foo.cs", 1, 100, HasContent: true, 20)));
    }

    [Fact]
    public void Classify_PresenceWinsOverProvableAbsenceAcrossReads()
    {
        Assert.Equal(
            ReviewCommentReadGrounding.Covered,
            Classify(
                "src/Foo.cs",
                50,
                new FileReadRecord("src/Foo.cs", 1, 100, HasContent: true, 20), // would prove absence for line 50
                new FileReadRecord("src/Foo.cs", 1, 100, HasContent: true, 60))); // but this one reaches line 50
    }

    [Fact]
    public void Classify_DegenerateWindow_IsNotCovering()
    {
        Assert.Equal(
            ReviewCommentReadGrounding.NotRead,
            Classify("src/Foo.cs", 30, new FileReadRecord("src/Foo.cs", 50, 10, HasContent: true, 0)));
    }

    // --- CreateReadRecord: the read is measured once, at the source, from raw content ---

    [Fact]
    public void CreateReadRecord_RealContent_ComputesLastLineFromWindowStart()
    {
        var record = ReviewReadGroundingEvaluator.CreateReadRecord("src/Foo.cs", 5, 100, Content(20));

        Assert.True(record.HasContent);
        Assert.Equal(5, record.StartLine);
        Assert.Equal(100, record.EndLine);
        Assert.Equal(24, record.LastLinePresent); // 5 + 20 - 1
        Assert.Equal("src/Foo.cs", record.NormalizedPath);
    }

    [Fact]
    public void CreateReadRecord_IgnoresASingleTrailingNewline()
    {
        var record = ReviewReadGroundingEvaluator.CreateReadRecord("src/Foo.cs", 1, 100, Content(20) + "\n");

        Assert.Equal(20, record.LastLinePresent); // the trailing newline is structure, not a 21st line
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[Binary file — content not available: src/Foo.cs]")]
    [InlineData("[File too large: 999999 bytes exceeds limit of 100000 bytes]")]
    public void CreateReadRecord_EmptyOrUnavailable_HasNoContent(string rawContent)
    {
        var record = ReviewReadGroundingEvaluator.CreateReadRecord("src/Foo.cs", 1, 100, rawContent);

        Assert.False(record.HasContent);
        Assert.Equal(0, record.LastLinePresent);
    }

    [Fact]
    public void CreateReadRecord_NormalizesPathSeparatorsAndLeadingSlash()
    {
        var record = ReviewReadGroundingEvaluator.CreateReadRecord("/src\\Foo.cs", 1, 10, Content(10));

        Assert.Equal("src/Foo.cs", record.NormalizedPath);
    }

    [Fact]
    public void CreateReadRecord_NonPositiveStartLine_ClampsWindowStartToOne()
    {
        var record = ReviewReadGroundingEvaluator.CreateReadRecord("src/Foo.cs", 0, 10, Content(3));

        Assert.Equal(1, record.StartLine);
        Assert.Equal(3, record.LastLinePresent); // 1 + 3 - 1
    }

    [Fact]
    public void CreateThenClassify_LineBeyondRealContent_IsMissing()
    {
        var record = ReviewReadGroundingEvaluator.CreateReadRecord("src/Foo.cs", 1, 100, Content(20));

        Assert.Equal(ReviewCommentReadGrounding.CitedLineMissing, Classify("src/Foo.cs", 50, record));
    }
}
