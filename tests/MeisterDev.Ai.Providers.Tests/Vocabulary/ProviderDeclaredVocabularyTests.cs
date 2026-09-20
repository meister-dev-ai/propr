// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.Tests.Vocabulary;

/// <summary>
///     What a family may put in its declared vocabulary, checked where it is set.
/// </summary>
/// <remarks>
///     A declared mode is stored against every connection of the family and read back by exact comparison, so a
///     value that names nothing is a row nothing resolves.
/// </remarks>
public sealed class ProviderDeclaredVocabularyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankAuthenticationModeIsRefused(string mode)
    {
        Assert.Throws<ArgumentException>(() => new ProviderDeclaredAuthMode(mode, [AiCredentialFieldSupport.ApiKey]));
    }

    [Fact]
    public void RenamingAnAuthenticationModeToABlankOneIsRefused()
    {
        var declared = new ProviderDeclaredAuthMode("acme/one:ApiKey", [AiCredentialFieldSupport.ApiKey]);

        Assert.Throws<ArgumentException>(() => declared with { Mode = "  " });
    }

    // A null reaches a comparison that dereferences it, and every reader of the list compares.
    [Fact]
    public void ANullProtocolModeIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, null!]));
    }

    // Two entries naming one mode offer it twice in every picker the declaration feeds, while a binding holding
    // it is one stored value.
    [Theory]
    [InlineData("acme/one:ChatCompletions", "acme/one:ChatCompletions")]
    [InlineData("acme/one:ChatCompletions", "ACME/ONE:CHATCOMPLETIONS")]
    [InlineData("Auto", "Auto")]
    public void ARepeatedProtocolModeIsRefused(string first, string second)
    {
        Assert.Throws<ArgumentException>(() => new ProviderDeclaredProtocolModes([first, second]));
    }

    [Fact]
    public void AnUnqualifiedProtocolModeThatIsNotReservedIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, "ChatCompletions"]));
    }

    // A mode is compared without regard to case everywhere else, so two keys differing only in case name one
    // mode with two labels. ToImmutableDictionary threw on that with a message naming neither the family nor the
    // mode, at host startup.
    [Fact]
    public void AModeLabelledTwiceIsRefusedNamingTheMode()
    {
        var refusal = Assert.Throws<ArgumentException>(() =>
            new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, "acme/one:ChatCompletions"])
            {
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["acme/one:ChatCompletions"] = "Chat Completions",
                    ["ACME/ONE:CHATCOMPLETIONS"] = "Chat completions",
                },
            });

        Assert.Contains("acme/one:ChatCompletions", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReservedModesAndOneOwnedModeAreAccepted()
    {
        var declared = new ProviderDeclaredProtocolModes(
        [
            ProviderDeclaredProtocolModes.Auto,
            ProviderDeclaredProtocolModes.Embeddings,
            "acme/one:ChatCompletions",
        ]);

        Assert.Equal(["acme/one:ChatCompletions"], declared.Owned);
        Assert.Equal(
            [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
            declared.ReservedHonoured);
    }
}
