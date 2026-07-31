using MeisterDev.ProPR.CodeInsights.Misses;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.CodeInsights.Tests.Misses;

/// <summary>
///     The load-bearing rule of the recall measurement: whether a human comment restates something ProPR
///     already raised. Getting it wrong in either direction corrupts the metric: a missed duplicate invents a
///     miss the reviewer did not make, and an over-eager match hides one it did.
/// </summary>
public sealed class HumanFindingOverlapTests
{
    private const string NullCheckFinding =
        "The `user` parameter is dereferenced without a null check, which will throw for an anonymous caller.";

    [Fact]
    public void AHumanCommentRestatingAFindingOnTheSameLine_IsADuplicate()
    {
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Service.cs",
            42,
            "user is dereferenced here without a null check: this will throw for an anonymous caller.",
            [Candidate("src/Service.cs", 42, NullCheckFinding)]);

        Assert.True(duplicate);
    }

    [Fact]
    public void AHumanCommentAFewLinesAway_IsStillADuplicate()
    {
        // A human rarely picks the exact line a machine chose, so nearby has to count.
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Service.cs",
            47,
            "user is dereferenced here without a null check: this will throw for an anonymous caller.",
            [Candidate("src/Service.cs", 42, NullCheckFinding)]);

        Assert.True(duplicate);
    }

    [Fact]
    public void AHumanCommentFarAwayInTheSameFile_IsNotADuplicate()
    {
        // Generic review phrasing repeats across a file; matching on wording alone would collapse unrelated
        // issues into one and silently erase real misses.
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Service.cs",
            420,
            "user is dereferenced here without a null check: this will throw for an anonymous caller.",
            [Candidate("src/Service.cs", 42, NullCheckFinding)]);

        Assert.False(duplicate);
    }

    [Fact]
    public void AHumanCommentInADifferentFile_IsNeverADuplicate()
    {
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Other.cs",
            42,
            "user is dereferenced here without a null check.",
            [Candidate("src/Service.cs", 42, NullCheckFinding)]);

        Assert.False(duplicate);
    }

    [Fact]
    public void AFileAnchoredCommentIsNotADuplicateOfAPullRequestLevelFinding()
    {
        // A summary remark would otherwise swallow every inline comment that shared its vocabulary.
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Service.cs",
            42,
            "user is dereferenced here without a null check.",
            [Candidate(null, null, NullCheckFinding)]);

        Assert.False(duplicate);
    }

    [Fact]
    public void TwoPullRequestLevelItemsAreComparedOnWordingAlone()
    {
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            null,
            null,
            "This change has no tests covering the new branch.",
            [Candidate(null, null, "The change adds a new branch with no tests covering it.")]);

        Assert.True(duplicate);
    }

    [Fact]
    public void ADifferentIssueOnTheSameLine_IsNotADuplicate()
    {
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Service.cs",
            42,
            "This method should be async: it blocks the request thread on I/O.",
            [Candidate("src/Service.cs", 42, NullCheckFinding)]);

        Assert.False(duplicate);
    }

    [Fact]
    public void WhenTheAnchorHasNoLine_TheFileAloneCarriesTheComparison()
    {
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Service.cs",
            null,
            "user is dereferenced without a null check and will throw for an anonymous caller.",
            [Candidate("src/Service.cs", 900, NullCheckFinding)]);

        Assert.True(duplicate);
    }

    [Fact]
    public void AnEmptyCommentIsNeverADuplicate()
    {
        Assert.False(
            HumanFindingOverlap.DuplicatesAnyFinding(
                "src/Service.cs",
                42,
                "   ",
                [Candidate("src/Service.cs", 42, NullCheckFinding)]));
    }

    [Fact]
    public void WithNoFindingsAtAll_NothingIsADuplicate()
    {
        // A pull request ProPR reviewed and found nothing in is precisely where misses matter most.
        Assert.False(HumanFindingOverlap.DuplicatesAnyFinding("src/Service.cs", 42, NullCheckFinding, []));
    }

    [Fact]
    public void AMatchAgainstAnyOneFindingIsEnough()
    {
        var duplicate = HumanFindingOverlap.DuplicatesAnyFinding(
            "src/Service.cs",
            42,
            "user is dereferenced here without a null check: this will throw for an anonymous caller.",
            [
                Candidate("src/Service.cs", 42, "Rename this local for clarity."),
                Candidate("src/Service.cs", 44, NullCheckFinding),
            ]);

        Assert.True(duplicate);
    }

    private static FindingOverlapCandidate Candidate(string? filePath, int? lineNumber, string message)
    {
        return new FindingOverlapCandidate(filePath, lineNumber, message);
    }
}
