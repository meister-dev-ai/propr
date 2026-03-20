using MeisterProPR.Application.Services;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="MentionDetector" />.</summary>
public sealed class MentionDetectorTests
{
    private static readonly Guid ReviewerGuid = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    [Fact]
    public void IsMentioned_WithAdoGuidFormat_ReturnsTrue()
    {
        var content = $"@<{ReviewerGuid}> What do you think about this PR?";
        Assert.True(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithUpperCaseGuid_ReturnsTrue()
    {
        var content = $"@<{ReviewerGuid.ToString().ToUpperInvariant()}> Is this correct?";
        Assert.True(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithLowerCaseGuid_ReturnsTrue()
    {
        var content = $"@<{ReviewerGuid.ToString().ToLowerInvariant()}> Is this correct?";
        Assert.True(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithDifferentGuid_ReturnsFalse()
    {
        var content = $"@<{Guid.NewGuid()}> Can you review?";
        Assert.False(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithEmptyContent_ReturnsFalse()
    {
        Assert.False(MentionDetector.IsMentioned(string.Empty, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_ContentWithoutMention_ReturnsFalse()
    {
        Assert.False(MentionDetector.IsMentioned("This is a regular comment with no mentions.", ReviewerGuid));
    }
}
