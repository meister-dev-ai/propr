// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.Tests.Hosting;

/// <summary>
///     The three guarantees a credential map used to carry in comments at its call sites.
/// </summary>
public sealed class ProviderCredentialValuesTests
{
    // A form posts every field it renders, so an empty box arrives as an empty string. Counting that as supplied
    // would store a credential of no characters and satisfy the required-field check with it.
    [Fact]
    public void ABlankValueIsNotSupplied()
    {
        var values = ProviderCredentialValues.From(
            new Dictionary<string, string>
            {
                ["accessKeyId"] = "AKIAEXAMPLE",
                ["secretAccessKey"] = "   ",
                ["sessionToken"] = string.Empty,
            });

        Assert.Equal(["accessKeyId"], values.Keys);
    }

    // A declared field name is matched ordinally everywhere else. A map built case-insensitively would answer a
    // presence check one way and the undeclared-name check the other.
    [Fact]
    public void ANameIsMatchedOrdinallyWhateverTheSourceMapCompared()
    {
        var supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["APIKEY"] = "a-key",
        };

        var values = ProviderCredentialValues.From(supplied);

        Assert.True(values.ContainsKey("APIKEY"));
        Assert.False(values.ContainsKey("apiKey"));
    }

    // These values are the secrets themselves, so the generated rendering of the map would print them.
    [Fact]
    public void RenderingNamesTheFieldsAndNoValues()
    {
        var rendered = ProviderCredentialValues.Single("apiKey", "sk-live-should-not-appear").ToString();

        Assert.Contains("apiKey", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-should-not-appear", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrAbsentMapIsTheSharedEmptyInstance()
    {
        Assert.Same(ProviderCredentialValues.None, ProviderCredentialValues.From(null));
        Assert.Same(ProviderCredentialValues.None, ProviderCredentialValues.From(new Dictionary<string, string>()));
        Assert.Same(
            ProviderCredentialValues.None,
            ProviderCredentialValues.From(new Dictionary<string, string> { ["apiKey"] = string.Empty }));
    }
}
