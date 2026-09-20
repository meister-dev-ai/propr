// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     The token rules an identity key and a qualified vocabulary value are held to, and the composition and
///     splitting that make a qualified value reversible.
/// </summary>
public sealed class ProviderVocabularyTests
{
    [Theory]
    [InlineData("meisterdev/openAi")]
    [InlineData("a/b")]
    [InlineData("Vendor-One/model-Family-2")]
    public void ValidateKey_ForAWellFormedKey_Accepts(string key)
    {
        Assert.Null(ProviderVocabulary.ValidateKey(key));
        Assert.True(ProviderVocabulary.IsValidIdentityKey(key));
    }

    [Theory]
    [InlineData("", ProviderVocabularyRule.Missing)]
    [InlineData(null, ProviderVocabularyRule.Missing)]
    [InlineData("meisterdev", ProviderVocabularyRule.Separator)]
    [InlineData("meisterdev/open/ai", ProviderVocabularyRule.Separator)]
    [InlineData("/openAi", ProviderVocabularyRule.Separator)]
    [InlineData("meisterdev/", ProviderVocabularyRule.Separator)]
    [InlineData("meisterdev/open_ai", ProviderVocabularyRule.Characters)]
    [InlineData("meisterdev/open ai", ProviderVocabularyRule.Characters)]
    [InlineData("meisterdev/openÄi", ProviderVocabularyRule.Characters)]
    [InlineData("meisterdev/open:ai", ProviderVocabularyRule.Characters)]
    public void ValidateKey_ForAKeyThatBreaksARule_NamesTheRuleAndTheValue(string? key, ProviderVocabularyRule rule)
    {
        var refusal = ProviderVocabulary.ValidateKey(key);

        Assert.NotNull(refusal);
        Assert.Equal(rule, refusal.Rule);
        Assert.NotEmpty(refusal.Message);

        // A refusal that does not carry the value leaves the author of a declaration to guess which of several
        // keys was rejected.
        if (!string.IsNullOrEmpty(key))
        {
            Assert.Contains(key, refusal.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidateKey_AtTheColumnWidth_AcceptsSixtyFourAndRefusesSixtyFive()
    {
        var sixtyFour = BuildKey(ProviderVocabulary.MaximumKeyLength);
        var sixtyFive = BuildKey(ProviderVocabulary.MaximumKeyLength + 1);

        Assert.Equal(ProviderVocabulary.MaximumKeyLength, sixtyFour.Length);
        Assert.Null(ProviderVocabulary.ValidateKey(sixtyFour));

        var refusal = ProviderVocabulary.ValidateKey(sixtyFive);
        Assert.NotNull(refusal);
        Assert.Equal(ProviderVocabularyRule.Length, refusal.Rule);
    }

    [Fact]
    public void ValidateQualifiedValue_AtTheColumnWidth_AcceptsOneHundredAndTwentyNineAndRefusesOneHundredAndThirty()
    {
        var key = BuildKey(ProviderVocabulary.MaximumKeyLength);
        var modeName = new string('m', ProviderVocabulary.MaximumModeNameLength);

        var longest = ProviderVocabulary.Compose(key, modeName);
        Assert.Equal(ProviderVocabulary.MaximumQualifiedValueLength, longest.Length);
        Assert.Equal(129, longest.Length);
        Assert.Null(ProviderVocabulary.ValidateQualifiedValue(longest));

        var refusal = ProviderVocabulary.ValidateQualifiedValue(longest + "m");
        Assert.NotNull(refusal);
        Assert.Equal(ProviderVocabularyRule.Length, refusal.Rule);
    }

    [Theory]
    [InlineData("meisterdev/openAi", "apiKey")]
    [InlineData("a/b", "c")]
    [InlineData("Vendor-One/model-Family-2", "chat-Completions-2")]
    public void Compose_ThenSplit_ReturnsTheOriginalParts(string key, string modeName)
    {
        var split = ProviderVocabulary.Split(ProviderVocabulary.Compose(key, modeName));

        Assert.True(split.IsQualified);
        Assert.Equal(key, split.Key);
        Assert.Equal(modeName, split.ModeName);
    }

    [Fact]
    public void Compose_ForAPartThatBreaksARule_RefusesNamingTheRule()
    {
        var badKey = Assert.Throws<ArgumentException>(() => ProviderVocabulary.Compose("meisterdev", "apiKey"));
        Assert.Contains("meisterdev", badKey.Message, StringComparison.Ordinal);

        var badMode = Assert.Throws<ArgumentException>(() => ProviderVocabulary.Compose("meisterdev/openAi", "api key"));
        Assert.Contains("api key", badMode.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("embeddings")]
    [InlineData("ApiKey")]
    public void Split_ForAValueWithNoQualifier_ReturnsNoKeyAndTheWholeValue(string value)
    {
        var split = ProviderVocabulary.Split(value);

        Assert.False(split.IsQualified);
        Assert.Null(split.Key);
        Assert.Equal(value, split.ModeName);
    }

    [Fact]
    public void Split_IgnoresSurroundingWhitespace()
    {
        var split = ProviderVocabulary.Split("  meisterdev/openAi:apiKey  ");

        Assert.Equal("meisterdev/openAi", split.Key);
        Assert.Equal("apiKey", split.ModeName);
    }

    [Fact]
    public void Split_ForAValueWithSeveralQualifierSeparators_SplitsAtTheFirst()
    {
        // The key's character set excludes the qualifier separator, so everything after the first one is the mode
        // name. A later separator makes that mode name ill-formed rather than moving the split.
        var split = ProviderVocabulary.Split("meisterdev/openAi:api:key");

        Assert.Equal("meisterdev/openAi", split.Key);
        Assert.Equal("api:key", split.ModeName);
        Assert.NotNull(ProviderVocabulary.ValidateModeName(split.ModeName));
    }

    [Fact]
    public void KeysEqual_ForKeysDifferingOnlyByCase_ComparesEqual()
    {
        Assert.True(ProviderVocabulary.KeysEqual("MeisterDev/OpenAI", "meisterdev/openai"));
        Assert.False(ProviderVocabulary.KeysEqual("meisterdev/openAi", "meisterdev/anthropic"));
        Assert.True(ProviderVocabulary.KeysEqual(null, null));
        Assert.False(ProviderVocabulary.KeysEqual(null, "meisterdev/openAi"));
    }

    [Fact]
    public void ADeclarationAndTheValidator_HoldOneKeyRule()
    {
        // The declaration's own check is the validator's, so a key accepted at declaration is a key that can be
        // composed, split and stored.
        var key = BuildKey(ProviderVocabulary.MaximumKeyLength);

        Assert.Equal(ProviderVocabulary.MaximumKeyLength, ProviderDeclaration.MaximumKeyLength);
        Assert.Equal(ProviderVocabulary.IsValidIdentityKey(key), ProviderDeclaration.IsValidIdentityKey(key));
        Assert.Equal(
            ProviderVocabulary.IsValidIdentityKey("meisterdev/open_ai"),
            ProviderDeclaration.IsValidIdentityKey("meisterdev/open_ai"));
    }

    [Fact]
    public void ADeclaredKeyThatBreaksARule_IsRefusedWithTheRuleItBroke()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new ProviderDeclaration
        {
            Key = "meisterdev/open_ai",
            Label = "Refused",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [],
            ProtocolModes = new ProviderDeclaredProtocolModes([]),
            ConformanceInputs = null!,
        });

        Assert.Equal(
            ProviderVocabulary.ValidateKey("meisterdev/open_ai")!.Message,
            refusal.Message.Split(" (Parameter")[0]);
    }

    private static string BuildKey(int length)
    {
        // A vendor prefix, the separator, and an internal name padded to the requested total.
        const string Prefix = "vendor/";
        return Prefix + new string('n', length - Prefix.Length);
    }
}
