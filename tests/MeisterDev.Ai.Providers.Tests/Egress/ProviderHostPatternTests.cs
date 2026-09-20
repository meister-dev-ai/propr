// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Egress;

namespace MeisterDev.Ai.Providers.Tests.Egress;

/// <summary>
///     Covers the two questions one host list answers: whether an entry matches a host, and whether an entry
///     covers a pattern a provider family declared it reaches.
/// </summary>
/// <remarks>
///     The containment question is the one worth pinning down, because getting its direction backwards turns a
///     restriction into a permission: an entry has to name at least every host the pattern admits, and checking
///     only that the two overlap would let a family declaring a whole domain through on a list naming one host
///     under it.
/// </remarks>
public sealed class ProviderHostPatternTests
{
    [Theory]
    [InlineData("api.openai.com", "api.openai.com")]
    [InlineData("API.OpenAI.com", "api.openai.com")]
    [InlineData("  api.openai.com/  ", "api.openai.com")]
    [InlineData("api.openai.com", "api.openai.com.")]
    [InlineData("api.openai.com.", "api.openai.com")]
    public void ABareEntryMatchesThatHostAndNoOther(string entry, string host)
    {
        Assert.True(ProviderHostPattern.DoesPatternMatchHost(entry, host));
        Assert.False(ProviderHostPattern.DoesPatternMatchHost(entry, "api.openai.com.evil.example"));
        Assert.False(ProviderHostPattern.DoesPatternMatchHost(entry, "sub.api.openai.com"));
    }

    [Fact]
    public void ASuffixEntryMatchesTheHostAndEverySubdomainOfIt()
    {
        Assert.True(ProviderHostPattern.DoesPatternMatchHost(".openai.azure.com", "contoso.openai.azure.com"));
        Assert.True(ProviderHostPattern.DoesPatternMatchHost(".openai.azure.com", "openai.azure.com"));
        Assert.False(ProviderHostPattern.DoesPatternMatchHost(".openai.azure.com", "notopenai.azure.com"));
        Assert.False(ProviderHostPattern.DoesPatternMatchHost(".openai.azure.com", "openai.azure.com.evil.example"));
    }

    [Fact]
    public void AnEmptyEntryOrHostMatchesNothing()
    {
        Assert.False(ProviderHostPattern.DoesPatternMatchHost(null, "api.openai.com"));
        Assert.False(ProviderHostPattern.DoesPatternMatchHost(string.Empty, "api.openai.com"));
        Assert.False(ProviderHostPattern.DoesPatternMatchHost("api.openai.com", null));
        Assert.False(ProviderHostPattern.DoesPatternMatchHost("api.openai.com", "   "));
    }

    [Fact]
    public void AnEntryEqualToAPatternCoversIt()
    {
        Assert.True(ProviderHostPattern.DoesEntryCoverPattern("api.openai.com", "api.openai.com"));
        Assert.True(ProviderHostPattern.DoesEntryCoverPattern(".openai.azure.com", ".openai.azure.com"));
    }

    // The direction that has to hold: an entry naming one host does not stand for a pattern admitting a whole
    // domain, and an entry naming the domain does stand for a host inside it.
    [Fact]
    public void ASuffixEntryCoversABareHostUnderItButNotTheReverse()
    {
        Assert.True(ProviderHostPattern.DoesEntryCoverPattern(".example.com", "api.example.com"));
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern("api.example.com", ".example.com"));
    }

    [Fact]
    public void ASuffixEntryCoversTheBareHostEqualToItWithoutTheLeadingDot()
    {
        Assert.True(ProviderHostPattern.DoesEntryCoverPattern(".example.com", "example.com"));
    }

    [Fact]
    public void ASuffixEntryCoversANarrowerSuffixPattern()
    {
        Assert.True(ProviderHostPattern.DoesEntryCoverPattern(".example.com", ".api.example.com"));
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(".api.example.com", ".example.com"));
    }

    // A bare entry names one host, and every suffix pattern admits more than one, so no bare entry can stand
    // for one.
    [Theory]
    [InlineData("example.com", ".example.com")]
    [InlineData("api.example.com", ".api.example.com")]
    [InlineData("example.com", ".com")]
    public void ABareEntryCoversNoSuffixPattern(string entry, string pattern)
    {
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(entry, pattern));
    }

    [Fact]
    public void ASuffixEntryDoesNotCoverAPatternThatMerelyContainsIt()
    {
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(".example.com", "notexample.com"));
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(".example.com", "example.com.evil.example"));
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(".example.com", ".example.com.evil.example"));
    }

    [Fact]
    public void ContainmentIgnoresCaseAndSurroundingNoise()
    {
        Assert.True(ProviderHostPattern.DoesEntryCoverPattern("  .Example.COM/ ", ".API.example.com"));
        Assert.True(ProviderHostPattern.DoesEntryCoverPattern(".Example.COM", "API.Example.com"));
    }

    [Fact]
    public void OneCoveringEntryIsEnoughAndNoneIsNot()
    {
        string[] entries = ["opencode.ai", ".example.com"];

        Assert.True(ProviderHostPattern.DoesAnyEntryCoverPattern(entries, ".api.example.com"));
        Assert.False(ProviderHostPattern.DoesAnyEntryCoverPattern(entries, ".other.example"));
        Assert.False(ProviderHostPattern.DoesAnyEntryCoverPattern(null, "api.example.com"));
        Assert.False(ProviderHostPattern.DoesAnyEntryCoverPattern([], "api.example.com"));
    }

    // A blank pattern names nothing, so nothing covers it. That keeps a family that declared one from being
    // waved past a restricted tenant on the strength of a value it left empty.
    [Fact]
    public void ABlankPatternIsCoveredByNothing()
    {
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(".example.com", string.Empty));
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(".example.com", "   "));
        Assert.False(ProviderHostPattern.DoesEntryCoverPattern(".example.com", null));
    }
}
