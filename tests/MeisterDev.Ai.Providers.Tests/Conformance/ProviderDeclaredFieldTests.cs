// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     What a family may declare a configuration field or an action input as.
/// </summary>
public sealed class ProviderDeclaredFieldTests
{
    // The name is a key in the settings document, in the credential envelope and in the submitted map, and the
    // label is what an operator sees where the value is entered. Neither survives being blank.
    [Theory]
    [InlineData("", "Callback URL")]
    [InlineData("   ", "Callback URL")]
    [InlineData("callbackUrl", "")]
    [InlineData("callbackUrl", "   ")]
    public void ABlankNameOrLabelIsRefused(string name, string label)
    {
        Assert.Throws<ArgumentException>(() => new ProviderDeclaredField(name, label, ProviderFieldKind.String));
    }

    // A `with` expression writes over what the constructor set, so the check has to be on the accessor too.
    [Fact]
    public void ABlankNameOrLabelIsRefusedOnACopyAsWell()
    {
        var field = new ProviderDeclaredField("callbackUrl", "Callback URL", ProviderFieldKind.String);

        Assert.Throws<ArgumentException>(() => field with { Name = " " });
        Assert.Throws<ArgumentException>(() => field with { Label = string.Empty });
    }

    [Fact]
    public void ANamedFieldKeepsWhatItWasDeclaredWith()
    {
        var field = new ProviderDeclaredField("callbackUrl", "Callback URL", ProviderFieldKind.Url);

        Assert.Equal("callbackUrl", field.Name);
        Assert.Equal("Callback URL", field.Label);
        Assert.Equal(ProviderFieldKind.Url, field.Kind);
    }
}
