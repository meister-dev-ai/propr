// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.ValueObjects;

/// <summary>
///     The output language is a language tag, not free text: a stored value either names a language every prose
///     prompt can state, or it reads as the default.
/// </summary>
public sealed class ReviewOutputLanguageTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hant-TW")]
    [InlineData("fil")]
    public void IsValidTag_AcceptsLanguageTags(string tag)
    {
        Assert.True(ReviewOutputLanguage.IsValidTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("e")]
    [InlineData("english please")]
    [InlineData("de_DE")]
    [InlineData("de-")]
    [InlineData("Deutsch, bitte übersetzen")]
    public void IsValidTag_RejectsAnythingElse(string? tag)
    {
        Assert.False(ReviewOutputLanguage.IsValidTag(tag));
    }

    [Fact]
    public void IsValidTag_RejectsATagLongerThanTheColumn()
    {
        var tooLong = "de" + string.Concat(Enumerable.Repeat("-Latn", ReviewOutputLanguage.MaxTagLength));

        Assert.True(tooLong.Length > ReviewOutputLanguage.MaxTagLength);
        Assert.False(ReviewOutputLanguage.IsValidTag(tooLong));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a tag")]
    public void Normalize_FallsBackToTheDefault(string? stored)
    {
        Assert.Equal(ReviewOutputLanguage.Default, ReviewOutputLanguage.Normalize(stored));
    }

    [Fact]
    public void Normalize_KeepsAValidTagAndTrimsIt()
    {
        Assert.Equal("pt-BR", ReviewOutputLanguage.Normalize("  pt-BR  "));
    }

    [Fact]
    public void Default_IsEnglish()
    {
        Assert.Equal("en", ReviewOutputLanguage.Default);
    }
}
